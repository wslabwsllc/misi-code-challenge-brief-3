using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace EventPermits.Mocks;

/// <summary>Fictional partner batch feed: drops CSV files into S3-compatible storage under partner-drop/.</summary>
public sealed class PartnerFeedService : IMockService
{
    public string Name => "partner-feed";

    public IReadOnlyList<string> Modes { get; } = [EventPermits.Mocks.Modes.Normal, EventPermits.Mocks.Modes.Offline, EventPermits.Mocks.Modes.Http500];

    public void ConfigureServices(IServiceCollection services, MockOptions options)
    {
        services.AddSingleton<PartnerFeedStore>();
        services.AddSingleton<IResettable>(sp => sp.GetRequiredService<PartnerFeedStore>());
        services.AddSingleton<PartnerFeedDropper>();
        if (options.PartnerFeed.ScheduleEnabled) services.AddHostedService<PartnerFeedScheduler>();
    }

    public void MapEndpoints(WebApplication app)
    {
        // Both endpoints need the admin key and, unlike the generic admin API, follow the scenario mode.
        app.MapPost("/_admin/partner-feed/drop", Drop)
            .AddEndpointFilter<AdminKeyFilter>()
            .AsServiceEndpoint()
            .WithName("DropPartnerFile")
            .WithSummary("Write one partner CSV (kind: clean, bad_rows, duplicate, updated, malformed_header). Requires X-Mock-Admin-Key.");
        app.MapGet("/files", (PartnerFeedStore store) => Results.Ok(store.History()))
            .AddEndpointFilter<AdminKeyFilter>()
            .AsServiceEndpoint()
            .WithName("ListPartnerFiles")
            .WithSummary("Files dropped by this process. Requires X-Mock-Admin-Key.");
    }

    private static async Task<IResult> Drop(DropRequest? body, PartnerFeedDropper dropper, ILogger<PartnerFeedDropper> logger, CancellationToken cancellationToken)
    {
        var kind = body?.Kind?.Trim().ToLowerInvariant();
        if (kind is null || !PartnerFeedDropper.Kinds.Contains(kind))
        {
            return Results.BadRequest(new { error = "unknown_kind", kinds = PartnerFeedDropper.Kinds });
        }

        try
        {
            var drop = await dropper.DropAsync(kind, cancellationToken);
            return Results.Ok(new { drop.Key, drop.Rows, drop.Sha256 });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var detail = PartnerFeedDropper.SafeDetail(ex);
            logger.LogWarning("Partner drop {Kind} failed: {Detail}", kind, detail);
            return Results.Json(new { error = "storage_unavailable", detail }, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}

public sealed record DropRequest(string? Kind);

public sealed record PartnerDrop(string Key, string Kind, int Rows, string Sha256, DateTime DroppedAt);

public sealed class PartnerFeedStore : IResettable
{
    private readonly object _gate = new();
    private readonly List<PartnerDrop> _drops = [];
    private byte[]? _lastClean;

    public void Add(PartnerDrop drop, byte[] content)
    {
        lock (_gate)
        {
            _drops.Add(drop);
            if (drop.Kind == "clean") _lastClean = content;
        }
    }

    public byte[]? LastClean
    {
        get { lock (_gate) return _lastClean; }
    }

    public IReadOnlyList<PartnerDrop> History()
    {
        lock (_gate) return _drops.ToList();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _drops.Clear();
            _lastClean = null;
        }
    }
}

/// <summary>Builds the CSV variants and writes them to the bucket.</summary>
public sealed class PartnerFeedDropper
{
    public const string Prefix = "partner-drop/";
    public const string Header = "partnerEventId,siteCode,startUtc,endUtc,attendeeCount,updatedAtUtc";
    public static readonly string[] Kinds = ["clean", "bad_rows", "duplicate", "updated", "malformed_header"];

    private static readonly int[] Durations = [60, 75, 90, 105, 120, 90];
    private static readonly int[] Attendees = [20, 45, 80, 110, 150, 64];

    private readonly PartnerFeedOptions _options;
    private readonly ReferenceData _data;
    private readonly PartnerFeedStore _store;
    private readonly ILogger<PartnerFeedDropper> _logger;
    private readonly DateTime _startedAt;
    private readonly Lazy<IAmazonS3> _s3;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public PartnerFeedDropper(MockOptions options, ReferenceData data, PartnerFeedStore store, MockStartup startup, ILogger<PartnerFeedDropper> logger)
    {
        _options = options.PartnerFeed;
        _data = data;
        _store = store;
        _logger = logger;
        _startedAt = startup.StartedAtUtc;
        // PublicationOnly: a failed client construction (e.g. no credentials yet) is retried on the next drop.
        _s3 = new Lazy<IAmazonS3>(CreateClient, LazyThreadSafetyMode.PublicationOnly);
    }

    public async Task<PartnerDrop> DropAsync(string kind, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Bucket)) throw new InvalidOperationException("Mock:PartnerFeed:Bucket is not configured.");

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (kind == "duplicate" && _store.LastClean is null) await WriteAsync("clean", Encode(CleanCsv()), cancellationToken);
            var content = kind switch
            {
                "clean" => Encode(CleanCsv()),
                "duplicate" => _store.LastClean!,
                "updated" => Encode(UpdatedCsv()),
                "bad_rows" => Encode(BadRowsCsv()),
                "malformed_header" => Encode(MalformedHeaderCsv()),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown drop kind."),
            };
            return await WriteAsync(kind, content, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<PartnerDrop> WriteAsync(string kind, byte[] content, CancellationToken cancellationToken)
    {
        var now = Clock.UtcNow();
        var key = $"{Prefix}partner-{now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}-{kind}.csv";
        using var stream = new MemoryStream(content, writable: false);
        await _s3.Value.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = stream,
            ContentType = "text/csv",
            // Plain (non aws-chunked) upload: the content is small and S3-compatible stores handle it reliably.
            UseChunkEncoding = false,
        }, cancellationToken);

        var rows = Encoding.UTF8.GetString(content).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;
        var drop = new PartnerDrop(key, kind, rows, Convert.ToHexStringLower(SHA256.HashData(content)), now);
        _store.Add(drop, content);
        _logger.LogInformation("Dropped partner file {Key} kind {Kind} rows {Rows} sha256 {Sha256}", key, kind, rows, drop.Sha256);
        return drop;
    }

    public static string SafeDetail(Exception ex) => ex switch
    {
        AmazonServiceException s => $"{(int)s.StatusCode} {s.ErrorCode}".Trim(),
        InvalidOperationException => ex.Message,
        _ => ex.GetType().Name,
    };

    private IAmazonS3 CreateClient()
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = _options.ForcePathStyle,
            Timeout = TimeSpan.FromSeconds(10),
            MaxErrorRetry = 1,
        };
        if (!string.IsNullOrWhiteSpace(_options.ServiceUrl))
        {
            config.ServiceURL = _options.ServiceUrl;
            config.AuthenticationRegion = _options.Region;
            // S3-compatible stores may not support the newer default checksum behaviour.
            config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(_options.Region);
        }

        return string.IsNullOrEmpty(_options.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(_options.AccessKey, _options.SecretKey), config);
    }

    private static byte[] Encode(string csv) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(csv);

    private static string Csv(string header, IEnumerable<string> rows) => header + "\n" + string.Concat(rows.Select(r => r + "\n"));

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Row(string id, string site, DateTime start, DateTime end, string attendees, DateTime updated) =>
        $"{id},{site},{Iso(start)},{Iso(end)},{attendees},{Iso(updated)}";

    /// <summary>Row i: days +2..+7 after the start date at 11:00 venue-local, fixed durations and attendee counts.</summary>
    private (string Site, DateTime Start, DateTime End) Slot(int i, int dayOffset)
    {
        var venue = _data.Venues[(i * 5) % _data.Venues.Count];
        var zone = _data.ZoneFor(venue.Code);
        var date = ReferenceData.LocalDate(_startedAt, zone).AddDays(dayOffset);
        var start = ReferenceData.LocalToUtc(date, new TimeOnly(11, 0), zone);
        return (venue.Code, start, start.AddMinutes(Durations[i % Durations.Length]));
    }

    private DateTime FixedUpdatedAt => new(_startedAt.Ticks - _startedAt.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private string CleanCsv() => Csv(Header, Enumerable.Range(0, 6).Select(i =>
    {
        var (site, start, end) = Slot(i, i + 2);
        return Row($"PRT-{i + 1:0000}", site, start, end, Attendees[i].ToString(CultureInfo.InvariantCulture), FixedUpdatedAt);
    }));

    private string UpdatedCsv()
    {
        var now = Clock.UtcNow();
        return Csv(Header, Enumerable.Range(0, 6).Select(i =>
        {
            var (site, start, end) = Slot(i, i + 2);
            var attendees = i % 2 == 0 ? Attendees[i] + 15 : Attendees[i];
            return Row($"PRT-{i + 1:0000}", site, start, end, attendees.ToString(CultureInfo.InvariantCulture), now);
        }));
    }

    private string BadRowsCsv()
    {
        var now = Clock.UtcNow();
        var rows = Enumerable.Range(0, 4).Select(i =>
        {
            var (site, start, end) = Slot(i + 1, i + 3);
            return Row($"PRT-{101 + i:0000}", site, start, end, Attendees[i].ToString(CultureInfo.InvariantCulture), now);
        }).ToList();
        var (s5, start5, end5) = Slot(0, 4);
        rows.Add(Row("PRT-0105", s5, end5, start5, "40", now)); // end before start
        var (_, start6, end6) = Slot(2, 5);
        rows.Add(Row("PRT-0106", "ZZZ-NOPE", start6, end6, "30", now)); // unknown site
        var (s7, start7, end7) = Slot(3, 6);
        rows.Add(Row("PRT-0107", s7, start7, end7, "many", now)); // non-numeric attendees
        return Csv(Header, rows);
    }

    private string MalformedHeaderCsv()
    {
        var now = Clock.UtcNow();
        return Csv("eventId,site,start,end,count,updated", Enumerable.Range(0, 2).Select(i =>
        {
            var (site, start, end) = Slot(i, i + 2);
            return Row($"PRT-{201 + i:0000}", site, start, end, Attendees[i].ToString(CultureInfo.InvariantCulture), now);
        }));
    }
}

/// <summary>Optional nightly clean drop at Mock:PartnerFeed:NightlyHourUtc.</summary>
public sealed class PartnerFeedScheduler(PartnerFeedDropper dropper, MockOptions options, ILogger<PartnerFeedScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hour = Math.Clamp(options.PartnerFeed.NightlyHourUtc, 0, 23);
        logger.LogInformation("Partner feed schedule enabled: one clean drop per day at {Hour}:00 UTC", hour);
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var next = now.Date.AddHours(hour);
            if (next <= now) next = next.AddDays(1);
            await Task.Delay(next - now, stoppingToken);
            try
            {
                await dropper.DropAsync("clean", stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Scheduled partner drop failed: {Detail}", PartnerFeedDropper.SafeDetail(ex));
            }
        }
    }
}
