using EventPermits.Domain;

namespace EventPermits.Application;

/// <summary>Correlation ID of the current HTTP request or UI operation (FR-16).</summary>
public interface ICorrelationContext
{
    string CorrelationId { get; }
}

/// <summary>
/// Correlation ID flowing with the async call chain, so outbound HTTP handlers (which live in their own DI scope)
/// see the ID of the request, UI operation or job that caused the call.
/// </summary>
public static class AmbientCorrelation
{
    private static readonly AsyncLocal<string?> Value = new();

    public static string? Current
    {
        get => Value.Value;
        set => Value.Value = value;
    }
}

/// <summary>Correlation for worker jobs and admin commands (no HTTP request).</summary>
public sealed class StaticCorrelationContext : ICorrelationContext
{
    private string _id = Guid.NewGuid().ToString("N");

    public string CorrelationId => _id;

    public string Begin()
    {
        _id = Guid.NewGuid().ToString("N");
        AmbientCorrelation.Current = _id;
        return _id;
    }
}

// ------------------------------------------------------------------ object storage (FR-15, brief §7.1)

public sealed record StoredObjectInfo(string Key, long Size, DateTimeOffset LastModified);

public interface IObjectStore
{
    Task PutAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken ct);
    Task<byte[]> GetAsync(string key, CancellationToken ct);
    /// <summary>Storage-native pre-signed GET URL valid for <paramref name="lifetime"/> (A-16 layer 2).</summary>
    string PresignGet(string key, TimeSpan lifetime, string downloadFileName, string contentType);
    Task<IReadOnlyList<StoredObjectInfo>> ListAsync(string prefix, CancellationToken ct);
    Task<bool> ExistsAsync(string key, CancellationToken ct);
    /// <summary>Removes an unreferenced staged object (orphan cleanup only; never business evidence).</summary>
    Task RemoveOrphanAsync(string key, CancellationToken ct);
    Task<bool> PingAsync(CancellationToken ct);
}

// ------------------------------------------------------------------ external systems (brief §7)

public sealed record RegistryRecord(string ReferenceId, string MatchedName, RegistryEligibility Eligibility, DateTimeOffset VerifiedAt);

public enum ExternalCallStatus { Ok, NotFound, RateLimited, Unauthorized, Conflict, Unavailable, Rejected }

public sealed record ExternalResult<T>(ExternalCallStatus Status, T? Value, string Detail, Guid? AttemptId, TimeSpan? RetryAfter = null)
{
    public bool Ok => Status == ExternalCallStatus.Ok;
}

public interface IApplicantRegistry
{
    Task<ExternalResult<RegistryRecord>> LookupAsync(string reference, Guid? applicationId, CancellationToken ct);
}

public sealed record CalendarEventDto(string ExternalId, string VenueCode, string Title, DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset ModifiedUtc, string? ReservationId);
public sealed record CalendarPage(IReadOnlyList<CalendarEventDto> Events, string? Cursor);
public sealed record CalendarConflictDto(string ExternalId, string Title, DateTimeOffset StartUtc, DateTimeOffset EndUtc, IReadOnlyList<DateTimeOffset> Alternatives);
public sealed record CalendarPushResult(string? ExternalId, CalendarConflictDto? Conflict);

public interface IRegionalCalendar
{
    Task<ExternalResult<CalendarPage>> GetEventsAsync(DateTimeOffset from, DateTimeOffset to, string? modifiedSince, CancellationToken ct);
    Task<ExternalResult<CalendarPushResult>> PushReservationAsync(Guid reservationId, Guid applicationId, string venueCode, DateTimeOffset start, DateTimeOffset end, string title, CancellationToken ct);
    Task<ExternalResult<bool>> ReleaseReservationAsync(Guid reservationId, Guid applicationId, CancellationToken ct);
    /// <summary>Token status for page 21: expiry only, never the token.</summary>
    CalendarTokenStatus TokenStatus { get; }
}

public sealed record CalendarTokenStatus(bool HasToken, DateTimeOffset? ExpiresAt, DateTimeOffset? AcquiredAt, string? LastError);

public sealed record GatewaySendResult(string MessageId, string Status);
public sealed record GatewayMessageStatus(string MessageId, string Status);

public interface INotificationGateway
{
    Task<ExternalResult<GatewaySendResult>> SendAsync(Notification message, CancellationToken ct);
    Task<ExternalResult<GatewayMessageStatus>> GetStatusAsync(string messageId, Guid? applicationId, CancellationToken ct);
}

public sealed record ForecastDto(string VenueCode, DateOnly Date, string Summary, double TemperatureC, double WindKph, int PrecipitationPct, WeatherAdvisory Advisory, DateTimeOffset SourceTimestampUtc);

public interface IWeatherService
{
    Task<ExternalResult<ForecastDto>> GetForecastAsync(string venueCode, DateOnly date, CancellationToken ct);
}

public sealed record GeocodeResultDto(string Label, double Latitude, double Longitude, double Confidence);

public interface IGeocoder
{
    Task<ExternalResult<IReadOnlyList<GeocodeResultDto>>> GeocodeAsync(string query, CancellationToken ct);
}

/// <summary>Administrative access to a mock service (Scenario Panel, call counts). Never exposed to browsers directly.</summary>
public interface IMockAdmin
{
    Task<ExternalResult<MockScenario>> GetScenarioAsync(IntegrationKind kind, CancellationToken ct);
    Task<ExternalResult<MockScenario>> SetScenarioAsync(IntegrationKind kind, string mode, CancellationToken ct);
    Task<ExternalResult<MockStats>> GetStatsAsync(IntegrationKind kind, CancellationToken ct);
    Task<ExternalResult<bool>> ResetAsync(IntegrationKind kind, CancellationToken ct);
    Task<ExternalResult<string>> DropPartnerFileAsync(string kind, CancellationToken ct);
    bool Supports(IntegrationKind kind);
}

public sealed record MockScenario(string Service, string Mode, IReadOnlyList<string> AvailableModes);
public sealed record MockStats(string Service, int Total, IReadOnlyDictionary<string, int> ByEndpoint, IReadOnlyDictionary<string, int> ByStatus);

/// <summary>Masked configuration of each integration for the hub (secrets never leave the server).</summary>
public interface IIntegrationSettings
{
    IReadOnlyList<IntegrationConfigView> Describe();
}

public sealed record IntegrationConfigView(IntegrationKind Kind, string Name, string BaseUrl, IReadOnlyDictionary<string, string> Masked);

// ------------------------------------------------------------------ documents and tokens

public sealed record PermitPdfModel(
    string PermitNumber, int Version, string ApplicationReference, string ApplicantName, string EventTitle, string Category,
    string VenueName, string CemeteryName, string LocalWindow, string TimeZone, int Attendees, string Conditions,
    string IssuedBy, DateTimeOffset IssuedAt, string Token, string ShortCode, string? ReissueReason);

public interface IPdfRenderer
{
    byte[] RenderPermit(PermitPdfModel model);
    byte[] RenderRunOfShow(string title, IReadOnlyList<string[]> rows, string footer);
}

/// <summary>HMAC signing for permit tokens (A-12) and download links (A-16).</summary>
public interface ISigner
{
    string Sign(string purpose, string payload);
    bool Verify(string purpose, string payload, string signature);
}
