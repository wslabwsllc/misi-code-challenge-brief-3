using System.Globalization;
using EventPermits.Application;
using EventPermits.Application.Services;
using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Infrastructure.Seeding;

public sealed partial class Seeder
{
    /// <summary>Venue closures, the regional calendar mirror, sync runs, cached forecasts and partner feed batches.</summary>
    private async Task SeedEnvironmentAsync(SeedWorld w, SeedPlan plan, CancellationToken ct)
    {
        await using var db = await Factory.CreateDbContextAsync(ct);
        db.Clock = Clock;
        db.ActorId = SeedMarker;
        var admin = w.Users.GetValueOrDefault("admin.india") ?? w.System;
        var anchor = w.Anchor;

        foreach (var b in plan.Blackouts)
        {
            if (await db.VenueBlackouts.AnyAsync(x => x.VenueId == b.Venue.Id && x.StartUtc == b.Start && x.Reason == b.Reason, ct)) continue;
            var created = Earliest(b.Start.AddDays(-3), anchor.AddDays(-20));
            var row = new VenueBlackout { VenueId = b.Venue.Id, StartUtc = b.Start, EndUtc = b.End, Reason = b.Reason, CreatedAt = created, CreatedBy = admin.Id };
            db.VenueBlackouts.Add(row);
            db.AuditEvents.Add(AuditRow(admin, created, "Blackout.Created", nameof(VenueBlackout), row.Id,
                $"Closure {b.Start:yyyy-MM-dd HH:mm}–{b.End:yyyy-MM-dd HH:mm} UTC: {b.Reason}"));
        }

        var imported = anchor.AddDays(-2);
        foreach (var e in plan.External)
        {
            if (await db.ExternalCalendarEvents.AnyAsync(x => x.ExternalId == e.ExternalId, ct)) continue;
            db.ExternalCalendarEvents.Add(new ExternalCalendarEvent
            {
                ExternalId = e.ExternalId, VenueCode = e.Venue.Code, VenueId = e.Venue.Id, Title = e.Title, StartUtc = e.Start, EndUtc = e.End,
                ModifiedUtc = imported.AddMinutes(-30), CreatedAt = imported.AddMinutes(1), CreatedBy = SeedMarker,
            });
        }

        if (!await db.CalendarSyncRuns.AnyAsync(r => r.CreatedBy == SeedMarker, ct))
        {
            var runs = new[]
            {
                new CalendarSyncRun { StartedAt = imported, CompletedAt = imported.AddSeconds(2), Trigger = "scheduled", Succeeded = true, Imported = plan.External.Count },
                new CalendarSyncRun { StartedAt = anchor.AddDays(-1).AddHours(-3), CompletedAt = anchor.AddDays(-1).AddHours(-3).AddSeconds(9), Trigger = "scheduled", Error = "Calendar unavailable: HTTP 503 Service Unavailable" },
                new CalendarSyncRun { StartedAt = anchor.AddMinutes(-20), CompletedAt = anchor.AddMinutes(-20).AddSeconds(2), Trigger = "scheduled", Succeeded = true, Unchanged = plan.External.Count },
            };
            foreach (var run in runs)
            {
                run.CreatedAt = run.StartedAt;
                run.CreatedBy = SeedMarker;
                db.CalendarSyncRuns.Add(run);
                db.AuditEvents.Add(run.Succeeded
                    ? AuditRow(w.System, run.CompletedAt!.Value, "Calendar.Synced", nameof(CalendarSyncRun), run.Id, $"Sync (scheduled): {run.Imported} new, {run.Updated} updated, {run.Unchanged} unchanged")
                    : AuditRow(w.System, run.CompletedAt!.Value, "Calendar.SyncFailed", nameof(CalendarSyncRun), run.Id, run.Error!));
            }
        }

        if (!await db.WeatherSnapshots.AnyAsync(x => x.CreatedBy == SeedMarker, ct))
        {
            var retrieved = anchor.AddMinutes(-50);
            var source = new DateTimeOffset(anchor.Year, anchor.Month, anchor.Day, anchor.Hour, 0, 0, TimeSpan.Zero).AddHours(-1);
            foreach (var wv in w.Weather.Venues.OrderBy(v => v.VenueCode, StringComparer.Ordinal))
            {
                var venue = w.Venues.FirstOrDefault(v => v.Code == wv.VenueCode);
                if (venue is null) continue;
                for (var d = 0; d <= 3; d++)
                {
                    // Same formula as the weather mock, so cached and live values agree.
                    var advisory = wv.SevereDays.Contains(d) ? WeatherAdvisory.Severe : wv.AdvisoryDays.Contains(d) ? WeatherAdvisory.Advisory : WeatherAdvisory.None;
                    var (summary, precipitation) = advisory switch
                    {
                        WeatherAdvisory.Severe => ("Thunderstorms with damaging winds", 90),
                        WeatherAdvisory.Advisory => ("Gusty winds and passing showers", 60),
                        _ => (d % 2 == 0 ? "Partly cloudy" : "Clear skies", 5 + d * 13 % 25),
                    };
                    var wind = wv.BaseWindKph + d * 3 % 4 * 2 + (advisory == WeatherAdvisory.Severe ? 45 : advisory == WeatherAdvisory.Advisory ? 20 : 0);
                    db.WeatherSnapshots.Add(new WeatherSnapshot
                    {
                        VenueId = venue.Id, VenueCode = venue.Code, Date = w.LocalToday(venue).AddDays(d), Summary = summary, TemperatureC = wv.BaseTemperatureC + d * 7 % 5 - 2,
                        WindKph = wind, PrecipitationPct = precipitation, Advisory = advisory, SourceTimestampUtc = source, RetrievedAt = retrieved,
                        CreatedAt = retrieved, CreatedBy = SeedMarker,
                    });
                }
            }
        }

        var codes = w.Venues.Select(v => v.Code).ToHashSet();
        var ids = w.Venues.ToDictionary(v => v.Code, v => v.Id);
        foreach (var b in plan.PartnerBatches)
        {
            if (await db.ImportBatches.AnyAsync(x => x.BatchId == b.BatchId, ct)) continue;
            var batch = new ImportBatch
            {
                BatchId = b.BatchId, Source = "drop-folder", FileName = b.FileName, Checksum = PartnerImportService.Checksum(b.Content), StartedAt = b.StartedAt,
                CompletedAt = b.StartedAt.AddSeconds(3), CreatedAt = b.StartedAt, CreatedBy = SeedMarker,
            };
            db.ImportBatches.Add(batch);
            if (b.Duplicate)
            {
                batch.Status = ImportBatchStatus.Duplicate;
                batch.MappingDiagnostics = ["Identical file content was already imported; nothing changed. Use Replay to process it again."];
                db.AuditEvents.Add(AuditRow(w.System, b.StartedAt, "Import.Duplicate", nameof(ImportBatch), batch.Id, $"{batch.BatchId}: duplicate of an earlier file ({b.FileName})"));
                continue;
            }
            var parsed = PartnerImportService.Parse(b.Content, codes);
            batch.MappingDiagnostics = parsed.Diagnostics.ToList();
            batch.RowsRead = parsed.Rows.Count;
            foreach (var row in parsed.Rows)
            {
                if (row.Error is not null)
                {
                    batch.Rejected++;
                    db.ImportRowErrors.Add(new ImportRowError { BatchId = batch.Id, RowNumber = row.Number, RawLine = row.Raw, Error = row.Error, CreatedAt = b.StartedAt, CreatedBy = SeedMarker });
                    continue;
                }
                var v = row.Value!;
                if (await db.PartnerEvents.AnyAsync(p => p.PartnerEventId == v.PartnerEventId, ct)) { batch.UnchangedRows++; continue; }
                db.PartnerEvents.Add(new PartnerEvent
                {
                    PartnerEventId = v.PartnerEventId, SiteCode = v.SiteCode, VenueId = ids[v.SiteCode], StartUtc = v.Start, EndUtc = v.End, AttendeeCount = v.Attendees,
                    UpdatedAtUtc = v.Updated, LastBatchId = batch.Id, CreatedAt = b.StartedAt, CreatedBy = SeedMarker,
                });
                batch.Inserted++;
            }
            batch.Status = batch.Rejected > 0 ? ImportBatchStatus.CompletedWithErrors : ImportBatchStatus.Completed;
            db.AuditEvents.Add(AuditRow(w.System, b.StartedAt.AddSeconds(3), "Import.Completed", nameof(ImportBatch), batch.Id,
                $"{batch.BatchId} (scheduled): {batch.Inserted} new, {batch.UpdatedRows} updated, {batch.UnchangedRows} unchanged, {batch.Rejected} rejected"));
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 35 sanitised IntegrationAttempts across all seven integrations (brief §9 "mock external responses") and
    /// 12 delivery webhook receipts (accepted, duplicate, rejected signature, rejected timestamp).
    /// </summary>
    private async Task SeedEvidenceAsync(SeedWorld w, SeedPlan plan, CancellationToken ct)
    {
        await using var db = await Factory.CreateDbContextAsync(ct);
        db.Clock = Clock;
        db.ActorId = SeedMarker;
        var anchor = w.Anchor;
        var attempts = new List<IntegrationAttempt>();
        var n = 0;
        string Base(string key, string fallback)
        {
            var url = Config[key];
            if (string.IsNullOrWhiteSpace(url)) url = fallback;
            return url.EndsWith('/') ? url : url + "/";
        }
        IntegrationAttempt Attempt(IntegrationKind kind, string operation, string method, string url, DateTimeOffset at, int? status, AttemptOutcome outcome,
            string? excerpt = null, Guid? applicationId = null, Guid? id = null, int attempt = 1, string? secretHeader = null)
        {
            var requestId = $"seed-att-{++n:D2}";
            var a = new IntegrationAttempt
            {
                Id = id ?? SeedIds.Guid(requestId), At = at, Integration = kind, Operation = operation, RequestId = requestId, CorrelationId = $"seed-corr-{n:D2}",
                ApplicationId = applicationId, Method = method, Url = url,
                RequestSummary = $"{method} {url}" + (secretHeader is null ? "" : $"\n{secretHeader}: ***") + $"\nX-Correlation-ID: seed-corr-{n:D2}\nX-Request-ID: {requestId}",
                StatusCode = status, Outcome = outcome, DurationMs = 40 + n * 37 % 400, AttemptNumber = attempt, ResponseExcerpt = excerpt,
                Error = outcome switch
                {
                    AttemptOutcome.Success => null,
                    AttemptOutcome.Timeout => "Timed out or cancelled",
                    AttemptOutcome.Offline => "Transport failure: ConnectionRefused",
                    _ => $"HTTP {status} {StatusText(status)}",
                },
            };
            attempts.Add(a);
            return a;
        }

        // Registry (8): the verifications recorded on submitted cases, including a retried 503 and a timeout.
        var registry = Base("Integrations:Registry:BaseUrl", "http://mock-registry:8080/");
        var verified = plan.Cases.Where(c => c.RegistryAttemptId is not null).OrderBy(c => c.No).ToList();
        for (var i = 0; i < verified.Count; i++)
        {
            var spec = verified[i];
            var verification = await db.RegistryVerifications.AsNoTracking().FirstOrDefaultAsync(v => v.AttemptId == spec.RegistryAttemptId, ct);
            var at = verification?.CreatedAt ?? anchor.AddDays(-1);
            var url = $"{registry}api/applicants/{spec.RegistryReference}";
            var body = $"{{\"referenceId\":\"{spec.RegistryReference}\",\"matchedName\":\"{spec.ApplicantName}\",\"eligibilityStatus\":\"{spec.Registry}\"}}";
            switch (spec.Registry)
            {
                case RegistryPlan.NotFound:
                    Attempt(IntegrationKind.Registry, "lookup", "GET", url, at, 404, AttemptOutcome.ClientError, "{\"error\":\"not_found\"}", verification?.ApplicationId, spec.RegistryAttemptId, secretHeader: "X-API-Key");
                    break;
                case RegistryPlan.Pending when spec.RegistryDetail?.Contains("rate", StringComparison.Ordinal) == true:
                    Attempt(IntegrationKind.Registry, "lookup", "GET", url, at, 429, AttemptOutcome.Transient, "Retry-After: 30\n\n{\"error\":\"rate_limited\"}", verification?.ApplicationId, spec.RegistryAttemptId, secretHeader: "X-API-Key");
                    break;
                case RegistryPlan.Pending:
                    Attempt(IntegrationKind.Registry, "lookup", "GET", url, at, null, AttemptOutcome.Timeout, null, verification?.ApplicationId, spec.RegistryAttemptId, secretHeader: "X-API-Key");
                    break;
                default:
                    if (i == 5)
                        Attempt(IntegrationKind.Registry, "lookup", "GET", url, at.AddSeconds(-1), 503, AttemptOutcome.Transient, "{\"error\":\"unavailable\"}", verification?.ApplicationId, secretHeader: "X-API-Key");
                    Attempt(IntegrationKind.Registry, "lookup", "GET", url, at, 200, AttemptOutcome.Success, body, verification?.ApplicationId, spec.RegistryAttemptId, i == 5 ? 2 : 1, "X-API-Key");
                    break;
            }
        }

        // Regional calendar (7): token, syncs (one failed), the conflicting push, a 503 push and an offline push.
        var calendar = Base("Integrations:Calendar:BaseUrl", "http://mock-calendar:8080/");
        var window = $"events?from={anchor.AddDays(-7):yyyy-MM-ddTHH:mm:ssZ}&to={anchor.AddDays(120):yyyy-MM-ddTHH:mm:ssZ}";
        Attempt(IntegrationKind.Calendar, "token", "POST", $"{calendar}oauth/token", anchor.AddDays(-2).AddMinutes(-1), 200, AttemptOutcome.Success, "{\"access_token\":\"***\",\"expires_in\":3600}");
        Attempt(IntegrationKind.Calendar, "list-events", "GET", $"{calendar}{window}", anchor.AddDays(-2), 200, AttemptOutcome.Success, $"{{\"events\":[… {plan.External.Count} events …]}}", secretHeader: "Authorization");
        Attempt(IntegrationKind.Calendar, "list-events", "GET", $"{calendar}{window}", anchor.AddDays(-1).AddHours(-3), 503, AttemptOutcome.Transient, "{\"error\":\"unavailable\"}", secretHeader: "Authorization");
        Attempt(IntegrationKind.Calendar, "list-events", "GET", $"{calendar}{window}", anchor.AddMinutes(-20), 200, AttemptOutcome.Success, $"{{\"events\":[… {plan.External.Count} events …]}}", secretHeader: "Authorization");
        async Task<(Guid? App, DateTimeOffset At)> CaseAsync(CaseSpec? spec)
        {
            if (spec is null) return (null, anchor.AddDays(-1));
            var app = await db.Applications.AsNoTracking().Where(a => a.ClientRequestKey == spec.Key).Select(a => new { a.Id }).FirstOrDefaultAsync(ct);
            var reservation = app is null ? null : await db.Reservations.AsNoTracking().Where(r => r.ApplicationId == app.Id).OrderBy(r => r.CreatedAt).FirstOrDefaultAsync(ct);
            return (app?.Id, reservation?.CreatedAt.AddMinutes(2) ?? anchor.AddDays(-1));
        }
        var conflict = await CaseAsync(plan.Cases.FirstOrDefault(c => c.CalendarConflict is not null));
        var ext = plan.Cases.FirstOrDefault(c => c.CalendarConflict is not null)?.CalendarConflict;
        Attempt(IntegrationKind.Calendar, "push-reservation", "POST", $"{calendar}reservations", conflict.At, 409, AttemptOutcome.ClientError,
            $"{{\"error\":\"conflict\",\"externalId\":\"{ext?.ExternalId}\"}}", conflict.App, secretHeader: "Authorization");
        var failedPa = await CaseAsync(plan.Cases.FirstOrDefault(c => c.Path == SeedPath.PendingApproval && c.Calendar == CalendarPushState.Failed));
        Attempt(IntegrationKind.Calendar, "push-reservation", "POST", $"{calendar}reservations", failedPa.At, 503, AttemptOutcome.Transient, "{\"error\":\"unavailable\"}", failedPa.App, secretHeader: "Authorization");
        var failedTr = await CaseAsync(plan.Cases.FirstOrDefault(c => c.Path == SeedPath.TentativelyReserved && c.Calendar == CalendarPushState.Failed));
        Attempt(IntegrationKind.Calendar, "push-reservation", "POST", $"{calendar}reservations", failedTr.At, null, AttemptOutcome.Offline, null, failedTr.App, secretHeader: "Authorization");

        // Notification gateway (7): accepted sends, failed sends (500, timeout) and one status poll.
        var notify = Base("Integrations:Notify:BaseUrl", "http://mock-notify:8080/");
        var sent = await db.Notifications.AsNoTracking().Where(x => x.IdempotencyKey.StartsWith("seed-ntf-") && x.GatewayMessageId != null).OrderBy(x => x.IdempotencyKey).ToListAsync(ct);
        var failed = await db.Notifications.AsNoTracking().Where(x => x.IdempotencyKey.StartsWith("seed-ntf-") && x.Status == NotificationStatus.Failed).OrderBy(x => x.IdempotencyKey).ToListAsync(ct);
        for (var i = 0; i < 3; i++)
        {
            var x = sent.ElementAtOrDefault(i);
            Attempt(IntegrationKind.NotificationGateway, "send", "POST", $"{notify}messages", x?.SentAt ?? anchor.AddHours(-30 + i), 202, AttemptOutcome.Success,
                $"{{\"messageId\":\"{x?.GatewayMessageId}\",\"status\":\"sent\"}}", x?.ApplicationId, secretHeader: "X-API-Key");
        }
        for (var i = 0; i < 3; i++)
        {
            var x = failed.ElementAtOrDefault(i);
            var at = x?.CreatedAt.AddMinutes(1 + i) ?? anchor.AddHours(-20 + i);
            if (i < 2) Attempt(IntegrationKind.NotificationGateway, "send", "POST", $"{notify}messages", at, 500, AttemptOutcome.Transient, "{\"error\":\"internal_error\"}", x?.ApplicationId, secretHeader: "X-API-Key");
            else Attempt(IntegrationKind.NotificationGateway, "send", "POST", $"{notify}messages", at, null, AttemptOutcome.Timeout, null, x?.ApplicationId, secretHeader: "X-API-Key");
        }
        var polled = sent.LastOrDefault(x => x.Status == NotificationStatus.Sent) ?? sent.LastOrDefault();
        Attempt(IntegrationKind.NotificationGateway, "status", "GET", $"{notify}messages/{polled?.GatewayMessageId ?? "unknown"}", anchor.AddMinutes(-35), 200, AttemptOutcome.Success,
            $"{{\"messageId\":\"{polled?.GatewayMessageId}\",\"status\":\"sent\"}}", polled?.ApplicationId, secretHeader: "X-API-Key");

        // Object storage (3): two document writes and a connection test.
        var storage = Base("Storage:ServiceUrl", "https://s3.us-east-1.amazonaws.com/");
        var bucket = Config["Storage:Bucket"] ?? "event-permits";
        var objects = await db.Attachments.AsNoTracking().OrderByDescending(a => a.UploadedAt).Take(2).ToListAsync(ct);
        foreach (var o in objects)
            Attempt(IntegrationKind.ObjectStorage, "put-object", "PUT", $"{storage}{bucket}/{o.ObjectKey}", o.UploadedAt, 200, AttemptOutcome.Success, null, o.ApplicationId);
        while (attempts.Count(a => a.Integration == IntegrationKind.ObjectStorage) < 2)
            Attempt(IntegrationKind.ObjectStorage, "put-object", "PUT", $"{storage}{bucket}/attachments/seed-check", anchor.AddHours(-3), 200, AttemptOutcome.Success);
        Attempt(IntegrationKind.ObjectStorage, "test-connection", "GET", $"{storage}{bucket}?list-type=2&max-keys=1", anchor.AddHours(-2), 200, AttemptOutcome.Success);

        // Weather (4): a severe forecast, a normal one, a 503 and a timeout (the page then shows the cached value).
        var weather = Base("Integrations:Weather:BaseUrl", "http://mock-weather:8080/");
        var severe = w.Weather.Venues.FirstOrDefault(v => v.SevereDays.Count > 0);
        var severeVenue = severe is null ? null : w.Venues.FirstOrDefault(v => v.Code == severe.VenueCode);
        var severeDate = severeVenue is null ? w.AnchorUtcDate : w.LocalToday(severeVenue).AddDays(severe!.SevereDays[0]);
        Attempt(IntegrationKind.Weather, "forecast", "GET", $"{weather}forecast?venue={severe?.VenueCode ?? "RMP-LAWN"}&date={severeDate:yyyy-MM-dd}", anchor.AddMinutes(-50), 200, AttemptOutcome.Success,
            "{\"advisory\":\"Severe\",\"summary\":\"Thunderstorms with damaging winds\"}");
        Attempt(IntegrationKind.Weather, "forecast", "GET", $"{weather}forecast?venue=CHG-CHAPEL&date={w.AnchorUtcDate:yyyy-MM-dd}", anchor.AddMinutes(-49), 200, AttemptOutcome.Success,
            "{\"advisory\":\"None\",\"summary\":\"Partly cloudy\"}");
        Attempt(IntegrationKind.Weather, "forecast", "GET", $"{weather}forecast?venue=MVR-LAWN&date={w.AnchorUtcDate.AddDays(1):yyyy-MM-dd}", anchor.AddMinutes(-15), 503, AttemptOutcome.Transient, "{\"error\":\"unavailable\"}");
        Attempt(IntegrationKind.Weather, "forecast", "GET", $"{weather}forecast?venue=MVR-LAWN&date={w.AnchorUtcDate.AddDays(1):yyyy-MM-dd}", anchor.AddMinutes(-14), null, AttemptOutcome.Timeout);

        // Partner feed (3): the drops behind the three seeded import batches.
        var partner = Base("Integrations:PartnerFeed:AdminUrl", "http://mock-partner-feed:8080/");
        foreach (var b in plan.PartnerBatches)
        {
            var kind = b.FileName.Contains("bad_rows", StringComparison.Ordinal) ? "bad_rows" : b.Duplicate ? "duplicate" : "clean";
            Attempt(IntegrationKind.PartnerFeed, "drop", "POST", $"{partner}admin/drop", b.StartedAt.AddMinutes(-5), 200, AttemptOutcome.Success,
                $"{{\"key\":\"partner-drop/{b.FileName}\",\"kind\":\"{kind}\"}}", secretHeader: "X-Mock-Admin-Key");
        }

        // Geocoding (3): one clear match, a tie and no match.
        var geocode = Base("Integrations:Geocode:BaseUrl", "http://mock-geocode:8080/");
        string Q(string text) => Uri.EscapeDataString(text);
        var first = w.Venues.FirstOrDefault();
        Attempt(IntegrationKind.Geocoding, "geocode", "GET", $"{geocode}geocode?q={Q(first?.Address ?? "100 Willow Bend Road")}", anchor.AddDays(-4), 200, AttemptOutcome.Success, "[{\"confidence\":0.97}]");
        Attempt(IntegrationKind.Geocoding, "geocode", "GET", $"{geocode}geocode?q={Q(w.Venues.ElementAtOrDefault(5)?.Address ?? "42 Lantern Hill Drive")}", anchor.AddDays(-4).AddMinutes(3), 200, AttemptOutcome.Success, "[{\"confidence\":0.71},{\"confidence\":0.71}]");
        Attempt(IntegrationKind.Geocoding, "geocode", "GET", $"{geocode}geocode?q={Q("1 Nowhere Lane, Practice State")}", anchor.AddDays(-4).AddMinutes(6), 200, AttemptOutcome.Success, "[]");

        foreach (var a in attempts)
            if (!await db.IntegrationAttempts.AnyAsync(x => x.RequestId == a.RequestId, ct)) db.IntegrationAttempts.Add(a);

        // Webhook receipts (12).
        var delivered = await db.Notifications.AsNoTracking().Where(x => x.IdempotencyKey.StartsWith("seed-ntf-") && x.Status == NotificationStatus.Delivered)
            .OrderBy(x => x.IdempotencyKey).Take(7).ToListAsync(ct);
        var receipts = new List<WebhookReceipt>();
        for (var i = 0; i < 7; i++)
        {
            var x = delivered.ElementAtOrDefault(i);
            var at = x?.DeliveredAt ?? anchor.AddHours(-10 + i);
            receipts.Add(x is null
                ? new WebhookReceipt { EventId = $"evt-seed-{i + 1:D2}", MessageId = $"msg-unknown-{i + 1:D2}", ReportedStatus = "delivered", OccurredAtUtc = at.AddSeconds(-5), ReceivedAt = at, Outcome = WebhookOutcome.Ignored, Reason = "Unknown message id." }
                : new WebhookReceipt { EventId = $"evt-seed-{i + 1:D2}", MessageId = x.GatewayMessageId, ReportedStatus = "delivered", OccurredAtUtc = at.AddSeconds(-5), ReceivedAt = at, Outcome = WebhookOutcome.Accepted, Reason = "Status set to Delivered.", NotificationId = x.Id });
        }
        foreach (var original in receipts.Take(2).ToList())
            receipts.Add(new WebhookReceipt { EventId = original.EventId, MessageId = original.MessageId, ReportedStatus = "delivered", OccurredAtUtc = original.OccurredAtUtc, ReceivedAt = original.ReceivedAt.AddMinutes(5), Outcome = WebhookOutcome.Duplicate, Reason = "Duplicate event ignored (idempotent).", NotificationId = original.NotificationId });
        receipts.Add(new WebhookReceipt { ReceivedAt = anchor.AddHours(-6), Outcome = WebhookOutcome.RejectedSignature, Reason = "Signature does not match." });
        receipts.Add(new WebhookReceipt { ReceivedAt = anchor.AddHours(-30), Outcome = WebhookOutcome.RejectedSignature, Reason = "Signature does not match." });
        receipts.Add(new WebhookReceipt { ReceivedAt = anchor.AddHours(-26), Outcome = WebhookOutcome.RejectedTimestamp, Reason = "Timestamp outside the 5-minute tolerance (replay protection)." });
        for (var i = 0; i < receipts.Count; i++)
        {
            var r = receipts[i];
            r.CorrelationId = $"seed-webhook-{i + 1:D2}";
            r.CreatedAt = r.ReceivedAt;
            r.CreatedBy = SeedMarker;
            if (!await db.WebhookReceipts.AnyAsync(x => x.CorrelationId == r.CorrelationId, ct)) db.WebhookReceipts.Add(r);
        }
        await db.SaveChangesAsync(ct);
    }

    private static string StatusText(int? status) => status switch
    {
        404 => "Not Found",
        409 => "Conflict",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "",
    };

    private static AuditEvent AuditRow(SeedUser actor, DateTimeOffset at, string action, string entityType, object entityId, string summary) => new()
    {
        At = at, ActorUserId = actor.Id, ActorName = actor.Name, Action = action, EntityType = entityType, EntityId = entityId.ToString() ?? "",
        Summary = summary.Length > 500 ? summary[..500] : summary, CorrelationId = $"seed-{action}-{at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}",
    };

    private static DateTimeOffset Earliest(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
