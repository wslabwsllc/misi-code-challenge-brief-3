using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public enum IntegrationHealth { Healthy, Degraded, Offline, Unknown }

public sealed record IntegrationCard(IntegrationKind Kind, string Name, IntegrationHealth Health, string HealthReason, int Requests24h, int Failures24h,
    DateTimeOffset? LastSuccess, DateTimeOffset? LastRequest, IReadOnlyList<IntegrationAttempt> RecentErrors, IntegrationConfigView Config, string DetailPage);

public sealed record ConnectionTest(IntegrationKind Kind, bool Ok, string Message, int DurationMs, Guid? AttemptId);

public sealed record ScenarioView(IntegrationKind Kind, string Name, string? Mode, IReadOnlyList<string> Modes, int? Calls, IReadOnlyDictionary<string, int> ByEndpoint, string? Error);

public sealed record AttemptDetail(IntegrationAttempt Attempt, string? ApplicationReference, IReadOnlyList<IntegrationAttempt> SameCorrelation, IReadOnlyList<AuditEvent> RelatedAudit);

/// <summary>External Integration Hub (E3-03): per-service health, counts, masked config, test connection, Scenario Panel, attempt detail.</summary>
public sealed partial class IntegrationHubService(AppDb dbs, ICorrelationContext correlation, IIntegrationSettings settings, IMockAdmin mocks, IObjectStore store,
    IApplicantRegistry registry, IRegionalCalendar calendar, INotificationGateway gateway, IWeatherService weather, IGeocoder geocoder)
    : ServiceBase(dbs, correlation)
{
    public static string Page(IntegrationKind kind) => kind switch
    {
        IntegrationKind.Registry => "/integrations/registry",
        IntegrationKind.Calendar => "/integrations/calendar",
        IntegrationKind.NotificationGateway => "/integrations/messaging",
        IntegrationKind.ObjectStorage => "/documents",
        IntegrationKind.Weather => "/integrations/weather",
        IntegrationKind.PartnerFeed => "/integrations/imports",
        IntegrationKind.Geocoding => "/venues/map",
        _ => "/integrations",
    };

    public async Task<IReadOnlyList<IntegrationCard>> CardsAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewIntegrations);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var since = Now.AddHours(-24);
        var cards = new List<IntegrationCard>();
        foreach (var config in settings.Describe())
        {
            if (config.Kind is IntegrationKind.ObjectStorage or IntegrationKind.PartnerFeed)
            {
                cards.Add(await LocalCardAsync(db, config, since, ct));
                continue;
            }
            var attempts = db.IntegrationAttempts.AsNoTracking().Where(a => a.Integration == config.Kind);
            var last = await attempts.OrderByDescending(a => a.At).FirstOrDefaultAsync(ct);
            var lastSuccess = await attempts.Where(a => a.Outcome == AttemptOutcome.Success).MaxAsync(a => (DateTimeOffset?)a.At, ct);
            var recent = await attempts.Where(a => a.At >= since).GroupBy(a => a.Outcome == AttemptOutcome.Success).Select(g => new { Ok = g.Key, Count = g.Count() }).ToListAsync(ct);
            var errors = await attempts.Where(a => a.Outcome != AttemptOutcome.Success).OrderByDescending(a => a.At).Take(5).ToListAsync(ct);
            var (health, reason) = Classify(last);
            cards.Add(new IntegrationCard(config.Kind, config.Name, health, reason, recent.Sum(r => r.Count), recent.Where(r => !r.Ok).Sum(r => r.Count),
                lastSuccess, last?.At, errors, config, Page(config.Kind)));
        }
        return cards;
    }

    private async Task<IntegrationCard> LocalCardAsync(AppDbContext db, IntegrationConfigView config, DateTimeOffset since, CancellationToken ct)
    {
        if (config.Kind == IntegrationKind.ObjectStorage)
        {
            var ok = await store.PingAsync(ct);
            var uploads = await db.Attachments.AsNoTracking().CountAsync(a => a.UploadedAt >= since, ct);
            var lastUpload = await db.Attachments.AsNoTracking().MaxAsync(a => (DateTimeOffset?)a.UploadedAt, ct);
            return new IntegrationCard(config.Kind, config.Name, ok ? IntegrationHealth.Healthy : IntegrationHealth.Offline,
                ok ? "Bucket reachable." : "Bucket not reachable.", uploads, 0, ok ? Now : lastUpload, lastUpload, [], config, Page(config.Kind));
        }
        var batches = db.ImportBatches.AsNoTracking();
        var lastBatch = await batches.OrderByDescending(b => b.StartedAt).FirstOrDefaultAsync(ct);
        var lastOk = await batches.Where(b => b.Status == ImportBatchStatus.Completed || b.Status == ImportBatchStatus.CompletedWithErrors).MaxAsync(b => (DateTimeOffset?)b.CompletedAt, ct);
        var count = await batches.CountAsync(b => b.StartedAt >= since, ct);
        var health = lastBatch switch
        {
            null => IntegrationHealth.Unknown,
            { Status: ImportBatchStatus.Failed } => IntegrationHealth.Degraded,
            { Status: ImportBatchStatus.CompletedWithErrors } => IntegrationHealth.Degraded,
            _ => IntegrationHealth.Healthy,
        };
        return new IntegrationCard(config.Kind, config.Name, health,
            lastBatch is null ? "No imports yet." : $"Last batch {lastBatch.BatchId}: {lastBatch.Status} ({lastBatch.Rejected} rejected row(s)).",
            count, await batches.CountAsync(b => b.StartedAt >= since && b.Status == ImportBatchStatus.Failed, ct), lastOk, lastBatch?.StartedAt, [], config, Page(config.Kind));
    }

    public static (IntegrationHealth, string) Classify(IntegrationAttempt? last) => last switch
    {
        null => (IntegrationHealth.Unknown, "No calls yet."),
        { Outcome: AttemptOutcome.Success } => (IntegrationHealth.Healthy, $"Last call succeeded ({last.Operation})."),
        { Outcome: AttemptOutcome.Offline or AttemptOutcome.Timeout } => (IntegrationHealth.Offline, $"Last call {last.Outcome.ToString().ToLowerInvariant()} ({last.Operation})."),
        _ => (IntegrationHealth.Degraded, $"Last call answered HTTP {last.StatusCode} ({last.Operation})."),
    };

    /// <summary>A real call to the integration, recorded like any other attempt (E3-03 AC2).</summary>
    public async Task<ConnectionTest> TestAsync(Actor actor, IntegrationKind kind, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewIntegrations);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        (bool ok, string message, Guid? attempt) result = kind switch
        {
            IntegrationKind.Registry => From(await registry.LookupAsync("REF-10001", null, ct), "Registry answered for REF-10001."),
            IntegrationKind.Calendar => From(await calendar.GetEventsAsync(Now, Now.AddDays(1), null, ct), "Token acquired and events listed."),
            IntegrationKind.NotificationGateway => GatewayResult(await gateway.GetStatusAsync("MSG-connection-test", null, ct)),
            IntegrationKind.Weather => From(await weather.GetForecastAsync("RMP-LAWN", DateOnly.FromDateTime(Now.UtcDateTime), ct), "Forecast returned for RMP-LAWN today."),
            IntegrationKind.Geocoding => From(await geocoder.GeocodeAsync("connection test", ct), "Geocoder answered."),
            IntegrationKind.ObjectStorage => (await store.PingAsync(ct), "Bucket list call.", null),
            IntegrationKind.PartnerFeed => (await store.PingAsync(ct), "Drop folder (object storage) reachable.", null),
            _ => (false, "Unknown integration.", null),
        };
        await using var db = await Dbs.CreateAsync(actor, ct);
        AuditLog(db, actor, "Integration.TestConnection", "Integration", kind, $"{kind} connection test: {(result.ok ? "ok" : "failed")} — {result.message}");
        await db.SaveChangesAsync(ct);
        return new ConnectionTest(kind, result.ok, result.message, (int)watch.ElapsedMilliseconds, result.attempt);
    }

    private static (bool, string, Guid?) From<T>(ExternalResult<T> r, string okMessage) => (r.Ok, r.Ok ? okMessage : $"{r.Status}: {r.Detail}", r.AttemptId);

    // A 404 for an unknown message id proves the gateway is reachable and authenticated.
    private static (bool, string, Guid?) GatewayResult(ExternalResult<GatewayMessageStatus> r) =>
        r.Status is ExternalCallStatus.Ok or ExternalCallStatus.NotFound ? (true, "Gateway reachable and API key accepted.", r.AttemptId) : (false, $"{r.Status}: {r.Detail}", r.AttemptId);

    public async Task<IReadOnlyList<ScenarioView>> ScenariosAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageIntegrations);
        var views = new List<ScenarioView>();
        foreach (var config in settings.Describe().Where(c => mocks.Supports(c.Kind)))
        {
            var scenario = await mocks.GetScenarioAsync(config.Kind, ct);
            var stats = await mocks.GetStatsAsync(config.Kind, ct);
            views.Add(new ScenarioView(config.Kind, config.Name, scenario.Value?.Mode, scenario.Value?.AvailableModes ?? [], stats.Value?.Total,
                stats.Value?.ByEndpoint ?? new Dictionary<string, int>(), scenario.Ok ? null : scenario.Detail));
        }
        return views;
    }

    public async Task SetScenarioAsync(Actor actor, IntegrationKind kind, string mode, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageIntegrations);
        var result = await mocks.SetScenarioAsync(kind, mode, ct);
        if (!result.Ok) throw new DependencyUnavailableException("MOCK_UNAVAILABLE", result.Detail);
        await using var db = await Dbs.CreateAsync(actor, ct);
        AuditLog(db, actor, "Integration.ScenarioChanged", "Integration", kind, $"{kind} scenario set to {mode}");
        await db.SaveChangesAsync(ct);
    }

    public async Task ResetMockAsync(Actor actor, IntegrationKind kind, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageIntegrations);
        var result = await mocks.ResetAsync(kind, ct);
        if (!result.Ok) throw new DependencyUnavailableException("MOCK_UNAVAILABLE", result.Detail);
        await using var db = await Dbs.CreateAsync(actor, ct);
        AuditLog(db, actor, "Integration.ScenarioReset", "Integration", kind, $"{kind} mock reset (mode normal, counters cleared)");
        await db.SaveChangesAsync(ct);
    }

    public async Task<(IReadOnlyList<IntegrationAttempt> Rows, int Total)> AttemptsAsync(Actor actor, IntegrationKind? kind, AttemptOutcome? outcome, string? correlationId, int page, CancellationToken ct = default)
    {
        if (!actor.Can(Cap.ViewIntegrations) && !actor.Can(Cap.ViewRegistry) && !actor.Can(Cap.SyncCalendar) && !actor.Can(Cap.ViewWeather) && !actor.Can(Cap.ManageImports) && !actor.Can(Cap.ViewMap))
            throw new ForbiddenException();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var q = db.IntegrationAttempts.AsNoTracking();
        if (kind is { } k) q = q.Where(a => a.Integration == k);
        if (outcome is { } o) q = q.Where(a => a.Outcome == o);
        if (!string.IsNullOrWhiteSpace(correlationId)) q = q.Where(a => a.CorrelationId == correlationId.Trim());
        var total = await q.CountAsync(ct);
        return (await q.OrderByDescending(a => a.At).Skip((Math.Max(1, page) - 1) * Limits.PageSize).Take(Limits.PageSize).ToListAsync(ct), total);
    }

    public async Task<AttemptDetail> AttemptAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        if (!actor.Can(Cap.ViewIntegrations) && !actor.Can(Cap.ViewRegistry) && !actor.Can(Cap.SyncCalendar) && !actor.Can(Cap.ViewWeather) && !actor.Can(Cap.ViewMap))
            throw new ForbiddenException();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var attempt = await db.IntegrationAttempts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException();
        var reference = attempt.ApplicationId is { } appId ? await db.Applications.AsNoTracking().Where(a => a.Id == appId).Select(a => a.Reference).FirstOrDefaultAsync(ct) : null;
        var same = string.IsNullOrEmpty(attempt.CorrelationId) ? [] :
            await db.IntegrationAttempts.AsNoTracking().Where(a => a.CorrelationId == attempt.CorrelationId).OrderBy(a => a.At).Take(50).ToListAsync(ct);
        var audit = string.IsNullOrEmpty(attempt.CorrelationId) ? [] :
            await db.AuditEvents.AsNoTracking().Where(a => a.CorrelationId == attempt.CorrelationId).OrderBy(a => a.At).Take(50).ToListAsync(ct);
        return new AttemptDetail(attempt, reference, same, audit);
    }
}

public sealed record WeatherView(Guid VenueId, string VenueCode, string VenueName, string TimeZoneId, DateOnly Date, WeatherSnapshot? Snapshot, bool Fresh, bool Stale,
    string? Problem, IReadOnlyList<CaseOnDate> Cases);
public sealed record CaseOnDate(Guid Id, string Reference, CaseStatus Status, DateTimeOffset StartUtc, bool Acknowledged, uint Version);

/// <summary>Weather &amp; Advisories (brief §7.1, E7-05): 15-minute cache; outage shows the cached result labelled Stale, never a fabricated forecast.</summary>
public sealed partial class WeatherQueryService(AppDb dbs, ICorrelationContext correlation, IWeatherService weather, RuleService rules) : ServiceBase(dbs, correlation)
{
    public async Task<WeatherView> ForecastAsync(Actor actor, Guid venueId, DateOnly date, bool refresh, CancellationToken ct = default)
    {
        if (!actor.Can(Cap.ViewWeather) && !actor.Can(Cap.ApproveCases)) throw new ForbiddenException();
        var cacheMinutes = await rules.IntAsync(RuleKeys.WeatherCacheMinutes, ct);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var venue = await db.Venues.AsNoTracking().Include(v => v.Cemetery).FirstOrDefaultAsync(v => v.Id == venueId, ct) ?? throw new NotFoundException();
        var cached = await db.WeatherSnapshots.Where(w => w.VenueId == venueId && w.Date == date).OrderByDescending(w => w.RetrievedAt).FirstOrDefaultAsync(ct);
        string? problem = null;
        if (refresh || cached is null || Now - cached.RetrievedAt > TimeSpan.FromMinutes(cacheMinutes))
        {
            var result = await weather.GetForecastAsync(venue.Code, date, ct);
            if (result.Ok)
            {
                var f = result.Value!;
                cached = new WeatherSnapshot
                {
                    VenueId = venue.Id, VenueCode = venue.Code, Date = date, Summary = f.Summary, TemperatureC = f.TemperatureC, WindKph = f.WindKph,
                    PrecipitationPct = f.PrecipitationPct, Advisory = f.Advisory, SourceTimestampUtc = f.SourceTimestampUtc, RetrievedAt = Now,
                };
                db.WeatherSnapshots.Add(cached);
                await db.SaveChangesAsync(ct);
            }
            else
            {
                problem = result.Status == ExternalCallStatus.RateLimited
                    ? "The weather service is rate limiting requests." + (result.RetryAfter is { } r ? $" Retry after {r.TotalSeconds:0} seconds." : "")
                    : "The weather service is unavailable: " + result.Detail;
            }
        }
        var fresh = cached is not null && Now - cached.RetrievedAt <= TimeSpan.FromMinutes(cacheMinutes);
        var zone = CaseService.Zone(venue.Cemetery!.TimeZoneId);
        var dayStart = CaseService.ToUtc(date, TimeOnly.MinValue, venue.Cemetery.TimeZoneId);
        var cases = await db.Reservations.AsNoTracking().Where(r => r.VenueId == venueId && r.Status != ReservationStatus.Released && r.StartUtc >= dayStart && r.StartUtc < dayStart.AddDays(1))
            .Join(db.Applications, r => r.ApplicationId, a => a.Id, (r, a) => new CaseOnDate(a.Id, a.Reference, a.Status, r.StartUtc, a.WeatherAcknowledgedAt != null, a.Version)).ToListAsync(ct);
        return new WeatherView(venue.Id, venue.Code, venue.Name, venue.Cemetery.TimeZoneId, date, cached, fresh, cached is not null && !fresh, problem, cases);
    }

    public static bool IsStale(DateTimeOffset retrievedAt, DateTimeOffset now, int cacheMinutes) => now - retrievedAt > TimeSpan.FromMinutes(cacheMinutes);
}

public sealed record GeocodeOutcome(Guid LookupId, bool Succeeded, string Message, IReadOnlyList<GeocodeCandidate> Candidates, bool RequiresChoice);

/// <summary>Geocoding (brief §7.1, E6-08): ties require a choice, coordinates persist only after confirmation, outage leaves them unverified.</summary>
public sealed partial class GeocodingService(AppDb dbs, ICorrelationContext correlation, IGeocoder geocoder) : ServiceBase(dbs, correlation)
{
    public async Task<GeocodeOutcome> LookupAsync(Actor actor, Guid venueId, CancellationToken ct = default)
    {
        actor.Require(Cap.ConfirmGeocode);
        string address;
        await using (var read = await Dbs.CreateAsync(actor, ct))
            address = (await read.Venues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == venueId, ct) ?? throw new NotFoundException()).Address;
        var result = await geocoder.GeocodeAsync(address, ct);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var lookup = new GeocodeLookup { VenueId = venueId, Query = address, Succeeded = result.Ok };
        if (result.Ok)
        {
            foreach (var c in result.Value!.OrderByDescending(c => c.Confidence))
                lookup.Candidates.Add(new GeocodeCandidate { Label = c.Label, Latitude = c.Latitude, Longitude = c.Longitude, Confidence = c.Confidence });
            lookup.Detail = lookup.Candidates.Count == 0 ? "No match. The venue keeps its address; coordinates stay unverified." : $"{lookup.Candidates.Count} candidate(s).";
        }
        else
        {
            lookup.Detail = "The geocoder is unavailable. The venue stays editable and its coordinates are flagged unverified.";
            var venue = await db.Venues.FirstAsync(v => v.Id == venueId, ct);
            if (venue.CoordinatesVerified) { venue.CoordinatesVerified = false; }
        }
        db.GeocodeLookups.Add(lookup);
        AuditLog(db, actor, "Geocode.Lookup", nameof(Venue), venueId, $"Geocode lookup: {lookup.Detail}");
        await db.SaveChangesAsync(ct);
        var top = lookup.Candidates.OrderByDescending(c => c.Confidence).ToList();
        var tie = top.Count > 1 && Math.Abs(top[0].Confidence - top[1].Confidence) < 0.0001;
        return new GeocodeOutcome(lookup.Id, result.Ok, lookup.Detail, top, tie);
    }

    public async Task ConfirmAsync(Actor actor, Guid candidateId, uint? venueVersion, CancellationToken ct = default)
    {
        actor.Require(Cap.ConfirmGeocode);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var candidate = await db.GeocodeCandidates.FirstOrDefaultAsync(c => c.Id == candidateId, ct) ?? throw new NotFoundException();
        var lookup = await db.GeocodeLookups.Include(l => l.Candidates).FirstAsync(l => l.Id == candidate.LookupId, ct);
        var venue = await db.Venues.FirstAsync(v => v.Id == lookup.VenueId, ct);
        ExpectVersion(db, venue, venueVersion);
        if (venue.Address != lookup.Query) throw new BusinessRuleException("GEOCODE_STALE", "The venue address changed after this lookup. Run the lookup again.");
        foreach (var c in lookup.Candidates) c.Selected = c.Id == candidate.Id;
        var before = new { venue.Latitude, venue.Longitude, venue.CoordinatesVerified };
        venue.Latitude = candidate.Latitude;
        venue.Longitude = candidate.Longitude;
        venue.GeocodeLabel = candidate.Label;
        venue.GeocodeConfidence = candidate.Confidence;
        venue.CoordinatesVerified = true;
        AuditLog(db, actor, "Geocode.Confirmed", nameof(Venue), venue.Id, $"Coordinates confirmed: {candidate.Label} ({candidate.Latitude:0.#####}, {candidate.Longitude:0.#####}, confidence {candidate.Confidence:0.00})",
            before: before, after: new { venue.Latitude, venue.Longitude, venue.CoordinatesVerified });
        await SaveAsync(db, ct);
    }

    public async Task<IReadOnlyList<GeocodeLookup>> HistoryAsync(Actor actor, Guid venueId, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewMap);
        await using var db = await Dbs.CreateAsync(actor, ct);
        return await db.GeocodeLookups.AsNoTracking().Include(l => l.Candidates).Where(l => l.VenueId == venueId).OrderByDescending(l => l.CreatedAt).Take(10).ToListAsync(ct);
    }
}

public sealed record ImportPreview(string FileName, string Checksum, IReadOnlyList<string> Header, IReadOnlyList<string> Diagnostics, IReadOnlyList<PreviewRow> Rows, bool HeaderValid, int TotalRows, bool AlreadyImported);
public sealed record PreviewRow(int Number, IReadOnlyList<string> Cells, string? Error);

/// <summary>Partner roster feed (brief §7.1, E10-01): drop folder or upload, schema validation, partial success, dedupe, replay.</summary>
public sealed partial class PartnerImportService(AppDb dbs, ICorrelationContext correlation, IObjectStore store, IMockAdmin mocks) : ServiceBase(dbs, correlation)
{
    public const string DropPrefix = "partner-drop/";
    public static readonly string[] Columns = ["partnerEventId", "siteCode", "startUtc", "endUtc", "attendeeCount", "updatedAtUtc"];

    public async Task<IReadOnlyList<StoredObjectInfo>> DropFilesAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageImports);
        return (await store.ListAsync(DropPrefix, ct)).OrderByDescending(f => f.LastModified).ToList();
    }

    public async Task<ImportPreview> PreviewAsync(Actor actor, string fileName, byte[] content, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageImports);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var venues = await db.Venues.AsNoTracking().Select(v => v.Code).ToListAsync(ct);
        var parsed = Parse(content, venues.ToHashSet());
        var checksum = Checksum(content);
        return new ImportPreview(fileName, checksum, parsed.Header, parsed.Diagnostics, parsed.Rows.Take(20).Select(r => new PreviewRow(r.Number, r.Cells, r.Error)).ToList(),
            parsed.HeaderValid, parsed.Rows.Count, await db.ImportBatches.AnyAsync(b => b.Checksum == checksum && b.Status != ImportBatchStatus.Failed, ct));
    }

    public async Task<ImportBatch> ImportUploadAsync(Actor actor, string fileName, byte[] content, bool replay, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageImports);
        if (content.Length == 0 || content.Length > 2 * 1024 * 1024) throw ValidationException.For("file", "Upload a CSV file up to 2 MB.");
        return await ImportAsync(actor, "upload", FileSignatures.SafeFileName(fileName), content, replay, ct);
    }

    /// <summary>Imports every not-yet-imported file in the drop folder (scheduled nightly, or "Fetch from drop folder").</summary>
    public async Task<IReadOnlyList<ImportBatch>> ImportDropFolderAsync(Actor actor, string trigger, CancellationToken ct = default)
    {
        if (actor != Actor.System) actor.Require(Cap.ManageImports);
        var batches = new List<ImportBatch>();
        foreach (var file in (await store.ListAsync(DropPrefix, ct)).OrderBy(f => f.LastModified))
        {
            await using (var db = await Dbs.CreateAsync(actor, ct))
                if (await db.ImportBatches.AnyAsync(b => b.Source == "drop-folder" && b.FileName == file.Key, ct)) continue;
            var content = await store.GetAsync(file.Key, ct);
            batches.Add(await ImportAsync(actor, "drop-folder", file.Key, content, replay: false, ct, trigger));
        }
        return batches;
    }

    public async Task<ImportBatch> ReplayAsync(Actor actor, Guid batchId, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageImports);
        ImportBatch original;
        await using (var db = await Dbs.CreateAsync(actor, ct))
            original = await db.ImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == batchId, ct) ?? throw new NotFoundException();
        if (original.Source != "drop-folder") throw new BusinessRuleException(ErrorCodes.NotRetryable, "Only drop-folder batches can be replayed; upload the file again to re-import it.");
        var content = await store.GetAsync(original.FileName, ct);
        return await ImportAsync(actor, "drop-folder", original.FileName, content, replay: true, ct);
    }

    public async Task<string> DropMockFileAsync(Actor actor, string kind, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageIntegrations);
        var result = await mocks.DropPartnerFileAsync(kind, ct);
        if (!result.Ok) throw new DependencyUnavailableException("MOCK_UNAVAILABLE", result.Detail);
        return result.Value!;
    }

    public async Task<(IReadOnlyList<ImportBatch> Rows, int Total)> BatchesAsync(Actor actor, int page, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageImports);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var total = await db.ImportBatches.CountAsync(ct);
        return (await db.ImportBatches.AsNoTracking().OrderByDescending(b => b.StartedAt).Skip((Math.Max(1, page) - 1) * Limits.PageSize).Take(Limits.PageSize).ToListAsync(ct), total);
    }

    public async Task<(ImportBatch Batch, IReadOnlyList<ImportRowError> Errors)> BatchAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageImports);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var batch = await db.ImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct) ?? throw new NotFoundException();
        return (batch, await db.ImportRowErrors.AsNoTracking().Where(e => e.BatchId == id).OrderBy(e => e.RowNumber).ToListAsync(ct));
    }

    public async Task<byte[]> ErrorReportAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        var (batch, errors) = await BatchAsync(actor, id, ct);
        var sb = new StringBuilder("batchId,rowNumber,error,rawLine\n");
        foreach (var e in errors) sb.AppendLine(Csv.Line([batch.BatchId, e.RowNumber.ToString(CultureInfo.InvariantCulture), e.Error, e.RawLine]));
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public async Task<IReadOnlyList<PartnerEventRow>> PartnerEventsAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageImports);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var events = await db.PartnerEvents.AsNoTracking().OrderBy(p => p.StartUtc).Take(200).ToListAsync(ct);
        var reservations = await db.Reservations.AsNoTracking().Where(r => r.Status != ReservationStatus.Released).Join(db.Applications, r => r.ApplicationId, a => a.Id, (r, a) => new { r, a.Reference }).ToListAsync(ct);
        return events.Select(p => new PartnerEventRow(p.PartnerEventId, p.SiteCode, p.StartUtc, p.EndUtc, p.AttendeeCount, p.UpdatedAtUtc,
            reservations.Where(x => x.r.VenueId == p.VenueId && x.r.BlockStartUtc < p.EndUtc && x.r.BlockEndUtc > p.StartUtc).Select(x => x.Reference).ToList())).ToList();
    }

    private async Task<ImportBatch> ImportAsync(Actor actor, string source, string fileName, byte[] content, bool replay, CancellationToken ct, string trigger = "manual")
    {
        await using var db = await Dbs.CreateAsync(actor, ct);
        var checksum = Checksum(content);
        var batch = new ImportBatch
        {
            BatchId = $"PB-{Now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}", Source = source, FileName = fileName, Checksum = checksum,
            StartedAt = Now, IsReplay = replay,
        };
        db.ImportBatches.Add(batch);
        // Same content imported before: no-op unless replay was chosen (A-17).
        if (!replay && await db.ImportBatches.AnyAsync(b => b.Checksum == checksum && (b.Status == ImportBatchStatus.Completed || b.Status == ImportBatchStatus.CompletedWithErrors), ct))
        {
            batch.Status = ImportBatchStatus.Duplicate;
            batch.CompletedAt = Now;
            batch.MappingDiagnostics = ["Identical file content was already imported; nothing changed. Use Replay to process it again."];
            AuditLog(db, actor, "Import.Duplicate", nameof(ImportBatch), batch.Id, $"{batch.BatchId}: duplicate of an earlier file ({fileName})");
            await db.SaveChangesAsync(ct);
            return batch;
        }
        var venues = await db.Venues.AsNoTracking().ToDictionaryAsync(v => v.Code, v => v.Id, ct);
        var parsed = Parse(content, venues.Keys.ToHashSet());
        batch.MappingDiagnostics = parsed.Diagnostics.ToList();
        batch.RowsRead = parsed.Rows.Count;
        if (!parsed.HeaderValid)
        {
            batch.Status = ImportBatchStatus.Failed;
            batch.Rejected = parsed.Rows.Count;
            foreach (var r in parsed.Rows) db.ImportRowErrors.Add(new ImportRowError { BatchId = batch.Id, RowNumber = r.Number, RawLine = r.Raw, Error = "Header does not match the expected columns." });
        }
        else
        {
            foreach (var row in parsed.Rows)
            {
                if (row.Error is not null)
                {
                    batch.Rejected++;
                    db.ImportRowErrors.Add(new ImportRowError { BatchId = batch.Id, RowNumber = row.Number, RawLine = row.Raw, Error = row.Error });
                    continue;
                }
                var v = row.Value!;
                var existing = await db.PartnerEvents.FirstOrDefaultAsync(p => p.PartnerEventId == v.PartnerEventId, ct);
                if (existing is null)
                {
                    db.PartnerEvents.Add(new PartnerEvent { PartnerEventId = v.PartnerEventId, SiteCode = v.SiteCode, VenueId = venues[v.SiteCode], StartUtc = v.Start, EndUtc = v.End,
                        AttendeeCount = v.Attendees, UpdatedAtUtc = v.Updated, LastBatchId = batch.Id });
                    batch.Inserted++;
                }
                else if (v.Updated > existing.UpdatedAtUtc)
                {
                    existing.SiteCode = v.SiteCode; existing.VenueId = venues[v.SiteCode]; existing.StartUtc = v.Start; existing.EndUtc = v.End;
                    existing.AttendeeCount = v.Attendees; existing.UpdatedAtUtc = v.Updated; existing.LastBatchId = batch.Id;
                    batch.UpdatedRows++;
                }
                else batch.UnchangedRows++;
            }
            batch.Status = batch.Rejected > 0 ? ImportBatchStatus.CompletedWithErrors : ImportBatchStatus.Completed;
        }
        batch.CompletedAt = Now;
        AuditLog(db, actor, "Import.Completed", nameof(ImportBatch), batch.Id,
            $"{batch.BatchId} ({trigger}{(replay ? ", replay" : "")}): {batch.Inserted} new, {batch.UpdatedRows} updated, {batch.UnchangedRows} unchanged, {batch.Rejected} rejected");
        await db.SaveChangesAsync(ct);
        return batch;
    }

    public static string Checksum(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public sealed record ParsedRow(int Number, string Raw, IReadOnlyList<string> Cells, string? Error, Row? Value);
    public sealed record Row(string PartnerEventId, string SiteCode, DateTimeOffset Start, DateTimeOffset End, int Attendees, DateTimeOffset Updated);
    public sealed record Parsed(IReadOnlyList<string> Header, bool HeaderValid, IReadOnlyList<string> Diagnostics, IReadOnlyList<ParsedRow> Rows);

    /// <summary>Validates the header and every row (A-17): required fields, ISO-8601 UTC, end after start, known site, attendees ≥ 1.</summary>
    public static Parsed Parse(byte[] content, IReadOnlySet<string> knownSites)
    {
        var text = Encoding.UTF8.GetString(content).TrimStart('﻿');
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        var diagnostics = new List<string>();
        if (lines.Count == 0) return new Parsed([], false, ["The file is empty."], []);
        var header = lines[0].Split(',').Select(h => h.Trim()).ToList();
        var valid = header.SequenceEqual(Columns, StringComparer.Ordinal);
        if (!valid)
        {
            var missing = Columns.Except(header).ToList();
            var extra = header.Except(Columns).ToList();
            diagnostics.Add($"Header mismatch. Expected: {string.Join(",", Columns)}.");
            if (missing.Count > 0) diagnostics.Add($"Missing columns: {string.Join(", ", missing)}.");
            if (extra.Count > 0) diagnostics.Add($"Unexpected columns: {string.Join(", ", extra)}.");
        }
        else diagnostics.Add("Header matches the expected six columns; mapped by name.");
        var rows = new List<ParsedRow>();
        for (var i = 1; i < lines.Count; i++)
        {
            var cells = lines[i].Split(',').Select(c => c.Trim()).ToList();
            if (!valid) { rows.Add(new ParsedRow(i + 1, lines[i], cells, "Header invalid", null)); continue; }
            string? error = null;
            Row? row = null;
            if (cells.Count != Columns.Length) error = $"Expected {Columns.Length} columns, found {cells.Count}.";
            else if (string.IsNullOrWhiteSpace(cells[0]) || cells[0].Length > 40) error = "partnerEventId is required (at most 40 characters).";
            else if (!knownSites.Contains(cells[1])) error = $"Unknown siteCode '{cells[1]}'.";
            else if (!Utc(cells[2], out var start)) error = "startUtc must be an ISO-8601 UTC time ending in Z.";
            else if (!Utc(cells[3], out var end)) error = "endUtc must be an ISO-8601 UTC time ending in Z.";
            else if (end <= start) error = "endUtc must be after startUtc.";
            else if (!int.TryParse(cells[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var attendees) || attendees < 1) error = "attendeeCount must be a whole number of at least 1.";
            else if (!Utc(cells[5], out var updated)) error = "updatedAtUtc must be an ISO-8601 UTC time ending in Z.";
            else row = new Row(cells[0], cells[1], start, end, attendees, updated);
            rows.Add(new ParsedRow(i + 1, lines[i], cells, error, row));
        }
        return new Parsed(header, valid, diagnostics, rows);
    }

    private static bool Utc(string value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result) && value.EndsWith('Z');
}

public sealed record PartnerEventRow(string PartnerEventId, string SiteCode, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int AttendeeCount, DateTimeOffset UpdatedAtUtc, IReadOnlyList<string> ConflictingReservations);
