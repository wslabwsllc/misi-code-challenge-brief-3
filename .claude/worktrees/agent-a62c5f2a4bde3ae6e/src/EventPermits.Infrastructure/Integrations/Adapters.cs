using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EventPermits.Application;
using EventPermits.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EventPermits.Infrastructure.Integrations;

/// <summary>Base URLs and credentials (configuration only; secrets from env vars or key-per-file, never in code).</summary>
public sealed class IntegrationOptions
{
    public EndpointOptions Registry { get; set; } = new();
    public CalendarOptions Calendar { get; set; } = new();
    public NotifyOptions Notify { get; set; } = new();
    public EndpointOptions Weather { get; set; } = new();
    public EndpointOptions Geocode { get; set; } = new();
    public PartnerFeedOptions PartnerFeed { get; set; } = new();
    public string MockAdminKey { get; set; } = "";

    public static IntegrationOptions From(IConfiguration config) => config.GetSection("Integrations").Get<IntegrationOptions>() ?? new();

    public class EndpointOptions
    {
        public string BaseUrl { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 5;
    }

    public sealed class CalendarOptions : EndpointOptions
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        /// <summary>Renew the token this long before it expires.</summary>
        public int RenewBeforeSeconds { get; set; } = 60;
    }

    public sealed class NotifyOptions : EndpointOptions
    {
        public string WebhookSecret { get; set; } = "";
    }

    public sealed class PartnerFeedOptions
    {
        public string AdminUrl { get; set; } = "";
        public string DropPrefix { get; set; } = "partner-drop/";
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static ExternalResult<T> Failure<T>(HttpStatusCode code, string detail, Guid? attempt, TimeSpan? retryAfter = null) =>
        new(Resilience.StatusOf(code), default, detail, attempt, retryAfter);

    public static ExternalResult<T> Unavailable<T>(string detail, Guid? attempt) =>
        new(ExternalCallStatus.Unavailable, default, detail, attempt);
}

/// <summary>Applicant Registry: REST + API key (brief §7.1).</summary>
public sealed class RegistryClient(HttpClient http, IntegrationOptions options) : IApplicantRegistry
{
    public async Task<ExternalResult<RegistryRecord>> LookupAsync(string reference, Guid? applicationId, CancellationToken ct)
    {
        using var request = Call.Request(HttpMethod.Get, $"api/applicants/{Uri.EscapeDataString(reference.Trim())}", IntegrationKind.Registry, "lookup", applicationId);
        request.Headers.Add("X-API-Key", options.Registry.ApiKey);
        try
        {
            using var response = await http.SendAsync(request, ct);
            var attempt = Call.AttemptId(request);
            if (!response.IsSuccessStatusCode)
                return Json.Failure<RegistryRecord>(response.StatusCode, response.StatusCode switch
                {
                    HttpStatusCode.NotFound => "No registry record for this reference.",
                    HttpStatusCode.TooManyRequests => "The registry is rate limiting requests.",
                    HttpStatusCode.Unauthorized => "The registry rejected the API key. An administrator must check the configuration.",
                    _ => $"The registry answered HTTP {(int)response.StatusCode}.",
                }, attempt, response.Headers.RetryAfter?.Delta);
            var body = await response.Content.ReadFromJsonAsync<RegistryBody>(Json.Options, ct)
                       ?? throw new JsonException("Empty registry response.");
            if (!Enum.TryParse<RegistryEligibility>(body.EligibilityStatus, true, out var eligibility) || string.IsNullOrWhiteSpace(body.ReferenceId))
                return new(ExternalCallStatus.Rejected, null, "The registry response was malformed.", attempt);
            return new(ExternalCallStatus.Ok, new RegistryRecord(body.ReferenceId, body.MatchedName ?? "", eligibility, body.VerifiedAt ?? DateTimeOffset.UtcNow), "found", attempt);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Polly.Timeout.TimeoutRejectedException or Polly.CircuitBreaker.BrokenCircuitException or JsonException)
        {
            return Json.Unavailable<RegistryRecord>(ex is JsonException ? "The registry response could not be read." : "The registry is unavailable right now.", Call.AttemptId(request));
        }
    }

    private sealed record RegistryBody(string ReferenceId, string? MatchedName, string EligibilityStatus, DateTimeOffset? VerifiedAt);
}

/// <summary>Server-side OAuth2 client-credentials token cache for the regional calendar (brief §7.1). Never exposes the token.</summary>
public sealed class CalendarTokenProvider(IHttpClientFactory httpFactory, IntegrationOptions options, TimeProvider clock, ILogger<CalendarTokenProvider> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;
    private DateTimeOffset? _acquiredAt;
    private string? _lastError;

    public CalendarTokenStatus Status => new(_token is not null && _expiresAt > clock.GetUtcNow(), _token is null ? null : _expiresAt, _acquiredAt, _lastError);

    public async Task<(string? Token, string? Error)> GetAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && _token is not null && clock.GetUtcNow() < _expiresAt.AddSeconds(-options.Calendar.RenewBeforeSeconds))
            return (_token, null);
        await _lock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _token is not null && clock.GetUtcNow() < _expiresAt.AddSeconds(-options.Calendar.RenewBeforeSeconds))
                return (_token, null);
            var http = httpFactory.CreateClient("calendar");
            using var request = Call.Request(HttpMethod.Post, "oauth/token", IntegrationKind.Calendar, "token");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.Calendar.ClientId,
                ["client_secret"] = options.Calendar.ClientSecret,
            });
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, ct);
                _token = null;
                _lastError = error == "invalid_client"
                    ? "The calendar rejected the client credentials (invalid_client). An administrator must update Integrations:Calendar:ClientId/ClientSecret."
                    : $"Token request failed with HTTP {(int)response.StatusCode}.";
                logger.LogWarning("Calendar token request failed: {Error}", error ?? response.StatusCode.ToString());
                return (null, _lastError);
            }
            var body = await response.Content.ReadFromJsonAsync<TokenBody>(Json.Options, ct) ?? throw new JsonException("Empty token response.");
            _token = body.AccessToken;
            _acquiredAt = clock.GetUtcNow();
            _expiresAt = _acquiredAt.Value.AddSeconds(Math.Max(body.ExpiresIn, 1));
            _lastError = null;
            return (_token, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate() => _token = null;

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record TokenBody(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType);
}

/// <summary>Regional Event Calendar: OAuth2 + REST. On 401 the token is refreshed and the call replayed once.</summary>
public sealed class CalendarClient(IHttpClientFactory httpFactory, CalendarTokenProvider tokens) : IRegionalCalendar
{
    public CalendarTokenStatus TokenStatus => tokens.Status;

    public async Task<ExternalResult<CalendarPage>> GetEventsAsync(DateTimeOffset from, DateTimeOffset to, string? modifiedSince, CancellationToken ct)
    {
        var query = $"events?from={Iso(from)}&to={Iso(to)}" + (string.IsNullOrEmpty(modifiedSince) ? "" : $"&modifiedSince={Uri.EscapeDataString(modifiedSince)}");
        return await SendAsync(() => Call.Request(HttpMethod.Get, query, IntegrationKind.Calendar, "list-events"), async response =>
        {
            var body = await response.Content.ReadFromJsonAsync<EventsBody>(Json.Options, ct) ?? throw new JsonException("Empty events response.");
            return new CalendarPage(body.Events.Select(e => new CalendarEventDto(e.ExternalId, e.VenueCode, e.Title ?? "", e.StartUtc, e.EndUtc, e.ModifiedUtc, e.ReservationId)).ToList(), body.Cursor);
        }, ct);
    }

    public Task<ExternalResult<CalendarPushResult>> PushReservationAsync(Guid reservationId, Guid applicationId, string venueCode, DateTimeOffset start, DateTimeOffset end, string title, CancellationToken ct) =>
        SendAsync(() =>
        {
            var request = Call.Request(HttpMethod.Post, "reservations", IntegrationKind.Calendar, "push-reservation", applicationId);
            request.Content = JsonContent.Create(new { reservationId = reservationId.ToString(), venueCode, startUtc = start, endUtc = end, title }, options: Json.Options);
            return request;
        }, async response =>
        {
            var body = await response.Content.ReadFromJsonAsync<PushBody>(Json.Options, ct);
            return new CalendarPushResult(body?.ExternalId, null);
        }, ct, async (response, attempt) =>
        {
            if (response.StatusCode != HttpStatusCode.Conflict) return null;
            var body = await response.Content.ReadFromJsonAsync<ConflictBody>(Json.Options, ct);
            var conflict = body?.ConflictingEvent;
            return new ExternalResult<CalendarPushResult>(ExternalCallStatus.Conflict,
                new CalendarPushResult(null, new CalendarConflictDto(conflict?.ExternalId ?? "", conflict?.Title ?? "Regional event",
                    conflict?.StartUtc ?? start, conflict?.EndUtc ?? end, body?.Alternatives ?? [])),
                "The regional calendar reports a conflicting event.", attempt);
        });

    public Task<ExternalResult<bool>> ReleaseReservationAsync(Guid reservationId, Guid applicationId, CancellationToken ct) =>
        SendAsync(() => Call.Request(HttpMethod.Delete, $"reservations/{reservationId}", IntegrationKind.Calendar, "release-reservation", applicationId),
            _ => Task.FromResult(true), ct);

    private async Task<ExternalResult<T>> SendAsync<T>(Func<HttpRequestMessage> build, Func<HttpResponseMessage, Task<T>> read, CancellationToken ct,
        Func<HttpResponseMessage, Guid?, Task<ExternalResult<T>?>>? special = null)
    {
        var http = httpFactory.CreateClient("calendar");
        Guid? attempt = null;
        try
        {
            for (var pass = 0; pass < 2; pass++)
            {
                var (token, error) = await tokens.GetAsync(forceRefresh: pass > 0, ct);
                if (token is null) return new(ExternalCallStatus.Unauthorized, default, error ?? "No calendar token.", attempt);
                using var request = build();
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using var response = await http.SendAsync(request, ct);
                attempt = Call.AttemptId(request);
                if (response.StatusCode == HttpStatusCode.Unauthorized && pass == 0)
                {
                    tokens.Invalidate(); // renew the token and replay once (brief §7.3)
                    continue;
                }
                if (special is not null && await special(response, attempt) is { } handled) return handled;
                if (!response.IsSuccessStatusCode)
                    return Json.Failure<T>(response.StatusCode, $"The calendar answered HTTP {(int)response.StatusCode}.", attempt);
                return new(ExternalCallStatus.Ok, await read(response), "ok", attempt);
            }
            return new(ExternalCallStatus.Unauthorized, default, "The calendar rejected a freshly renewed token.", attempt);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Polly.Timeout.TimeoutRejectedException or Polly.CircuitBreaker.BrokenCircuitException or JsonException)
        {
            return Json.Unavailable<T>("The regional calendar is unavailable right now.", attempt);
        }
    }

    private static string Iso(DateTimeOffset t) => Uri.EscapeDataString(t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

    private sealed record EventsBody(List<EventBody> Events, string? Cursor);
    private sealed record EventBody(string ExternalId, string VenueCode, string? Title, DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset ModifiedUtc, string? ReservationId);
    private sealed record PushBody(string? ExternalId);
    private sealed record ConflictBody(ConflictEventBody? ConflictingEvent, List<DateTimeOffset>? Alternatives);
    private sealed record ConflictEventBody(string? ExternalId, string? Title, DateTimeOffset StartUtc, DateTimeOffset EndUtc);
}

/// <summary>Notification Gateway: POST /messages with an idempotency key; GET /messages/{id} for status.</summary>
public sealed class NotificationGatewayClient(HttpClient http, IntegrationOptions options) : INotificationGateway
{
    public async Task<ExternalResult<GatewaySendResult>> SendAsync(Notification message, CancellationToken ct)
    {
        using var request = Call.Request(HttpMethod.Post, "messages", IntegrationKind.NotificationGateway, "send", message.ApplicationId);
        request.Headers.Add("X-API-Key", options.Notify.ApiKey);
        request.Headers.Add("Idempotency-Key", message.IdempotencyKey);
        request.Content = JsonContent.Create(new { to = message.Recipient, channel = message.Channel.ToString(), subject = message.Subject, body = message.Body }, options: Json.Options);
        try
        {
            using var response = await http.SendAsync(request, ct);
            var attempt = Call.AttemptId(request);
            if (!response.IsSuccessStatusCode)
                return Json.Failure<GatewaySendResult>(response.StatusCode, $"The gateway answered HTTP {(int)response.StatusCode}.", attempt);
            var body = await response.Content.ReadFromJsonAsync<SendBody>(Json.Options, ct) ?? throw new JsonException("Empty gateway response.");
            return new(ExternalCallStatus.Ok, new GatewaySendResult(body.MessageId, body.Status ?? "Queued"), "accepted", attempt);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Polly.Timeout.TimeoutRejectedException or Polly.CircuitBreaker.BrokenCircuitException or JsonException)
        {
            return Json.Unavailable<GatewaySendResult>("The notification gateway is unavailable right now.", Call.AttemptId(request));
        }
    }

    public async Task<ExternalResult<GatewayMessageStatus>> GetStatusAsync(string messageId, Guid? applicationId, CancellationToken ct)
    {
        using var request = Call.Request(HttpMethod.Get, $"messages/{Uri.EscapeDataString(messageId)}", IntegrationKind.NotificationGateway, "status", applicationId);
        request.Headers.Add("X-API-Key", options.Notify.ApiKey);
        try
        {
            using var response = await http.SendAsync(request, ct);
            var attempt = Call.AttemptId(request);
            if (!response.IsSuccessStatusCode)
                return Json.Failure<GatewayMessageStatus>(response.StatusCode, $"The gateway answered HTTP {(int)response.StatusCode}.", attempt);
            var body = await response.Content.ReadFromJsonAsync<SendBody>(Json.Options, ct) ?? throw new JsonException("Empty gateway response.");
            return new(ExternalCallStatus.Ok, new GatewayMessageStatus(body.MessageId, body.Status ?? ""), "ok", attempt);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Polly.Timeout.TimeoutRejectedException or Polly.CircuitBreaker.BrokenCircuitException or JsonException)
        {
            return Json.Unavailable<GatewayMessageStatus>("The notification gateway is unavailable right now.", Call.AttemptId(request));
        }
    }

    private sealed record SendBody(string MessageId, string? Status);
}

/// <summary>Weather Advisory API: third-party read-only REST (no retries beyond the shared policy).</summary>
public sealed class WeatherClient(HttpClient http) : IWeatherService
{
    public async Task<ExternalResult<ForecastDto>> GetForecastAsync(string venueCode, DateOnly date, CancellationToken ct)
    {
        using var request = Call.Request(HttpMethod.Get, $"forecast?venue={Uri.EscapeDataString(venueCode)}&date={date:yyyy-MM-dd}", IntegrationKind.Weather, "forecast");
        try
        {
            using var response = await http.SendAsync(request, ct);
            var attempt = Call.AttemptId(request);
            if (!response.IsSuccessStatusCode)
                return Json.Failure<ForecastDto>(response.StatusCode, $"The weather service answered HTTP {(int)response.StatusCode}.", attempt, response.Headers.RetryAfter?.Delta);
            var b = await response.Content.ReadFromJsonAsync<ForecastBody>(Json.Options, ct) ?? throw new JsonException("Empty forecast.");
            if (!Enum.TryParse<WeatherAdvisory>(b.Advisory, true, out var advisory)) throw new JsonException("Unknown advisory.");
            return new(ExternalCallStatus.Ok, new ForecastDto(b.VenueCode, DateOnly.Parse(b.Date, CultureInfo.InvariantCulture), b.Summary ?? "", b.TemperatureC, b.WindKph, b.PrecipitationPct, advisory, b.SourceTimestampUtc), "ok", attempt);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Polly.Timeout.TimeoutRejectedException or Polly.CircuitBreaker.BrokenCircuitException or JsonException or FormatException)
        {
            return Json.Unavailable<ForecastDto>("The weather service is unavailable right now.", Call.AttemptId(request));
        }
    }

    private sealed record ForecastBody(string VenueCode, string Date, string? Summary, double TemperatureC, double WindKph, int PrecipitationPct, string Advisory, DateTimeOffset SourceTimestampUtc);
}

/// <summary>Location Service: GET /geocode?q= returning candidates with confidence.</summary>
public sealed class GeocodeClient(HttpClient http) : IGeocoder
{
    public async Task<ExternalResult<IReadOnlyList<GeocodeResultDto>>> GeocodeAsync(string query, CancellationToken ct)
    {
        using var request = Call.Request(HttpMethod.Get, $"geocode?q={Uri.EscapeDataString(query)}", IntegrationKind.Geocoding, "geocode");
        try
        {
            using var response = await http.SendAsync(request, ct);
            var attempt = Call.AttemptId(request);
            if (!response.IsSuccessStatusCode)
                return Json.Failure<IReadOnlyList<GeocodeResultDto>>(response.StatusCode, $"The geocoder answered HTTP {(int)response.StatusCode}.", attempt);
            var body = await response.Content.ReadFromJsonAsync<GeoBody>(Json.Options, ct) ?? throw new JsonException("Empty geocode response.");
            var results = body.Results.Where(r => r.Confidence is >= 0 and <= 1 && Math.Abs(r.Latitude) <= 90 && Math.Abs(r.Longitude) <= 180)
                .Select(r => new GeocodeResultDto(r.Label, r.Latitude, r.Longitude, r.Confidence)).ToList();
            return new(ExternalCallStatus.Ok, results, "ok", attempt);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Polly.Timeout.TimeoutRejectedException or Polly.CircuitBreaker.BrokenCircuitException or JsonException)
        {
            return Json.Unavailable<IReadOnlyList<GeocodeResultDto>>("The geocoder is unavailable right now.", Call.AttemptId(request));
        }
    }

    private sealed record GeoBody(List<GeoResult> Results);
    private sealed record GeoResult(string Label, double Latitude, double Longitude, double Confidence);
}
