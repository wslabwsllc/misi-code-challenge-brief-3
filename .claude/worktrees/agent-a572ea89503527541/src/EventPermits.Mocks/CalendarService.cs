using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace EventPermits.Mocks;

/// <summary>Fictional regional calendar: OAuth2 client credentials, event listing and reservation holds.</summary>
public sealed class CalendarService : IMockService
{
    public const string InvalidClient = "invalid_client";
    public const string ShortToken = "short_token";
    public const string UnauthorizedOnce = "unauthorized_once";
    public const string Conflict = "conflict";

    public string Name => "calendar";

    public IReadOnlyList<string> Modes { get; } = [.. EventPermits.Mocks.Modes.Common, InvalidClient, ShortToken, UnauthorizedOnce, Conflict];

    public void ConfigureServices(IServiceCollection services, MockOptions options)
    {
        services.AddSingleton<CalendarStore>();
        services.AddSingleton<IResettable>(sp => sp.GetRequiredService<CalendarStore>());
    }

    public void MapEndpoints(WebApplication app)
    {
        app.MapPost("/oauth/token", Token)
            .AsServiceEndpoint()
            .DisableAntiforgery()
            .WithName("IssueToken")
            .WithSummary("OAuth2 client credentials grant (form body or HTTP Basic client authentication).");
        app.MapGet("/events", ListEvents)
            .AsServiceEndpoint()
            .WithName("ListEvents")
            .WithSummary("Events overlapping [from, to), optionally only those modified after modifiedSince. Bearer token required.");
        app.MapPost("/reservations", Reserve)
            .AsServiceEndpoint()
            .WithName("CreateReservation")
            .WithSummary("Create or update a hold. 409 with alternatives when it overlaps another event at the venue. Bearer token required.");
        app.MapDelete("/reservations/{reservationId}", Release)
            .AsServiceEndpoint()
            .WithName("DeleteReservation")
            .WithSummary("Remove a hold (idempotent). Bearer token required.");
    }

    private static async Task<IResult> Token(HttpRequest request, ScenarioState scenario, MockOptions options, CalendarStore store)
    {
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "invalid_request" });
        var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);

        string? clientId = form["client_id"];
        string? clientSecret = form["client_secret"];
        if (AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var auth)
            && string.Equals(auth.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            && TryDecodeBasic(auth.Parameter, out var basicId, out var basicSecret))
        {
            clientId = basicId;
            clientSecret = basicSecret;
        }

        var mode = scenario.Mode;
        var credentialsValid = Secrets.Matches(clientId, options.Calendar.ClientId) && Secrets.Matches(clientSecret, options.Calendar.ClientSecret);
        if (mode == InvalidClient || !credentialsValid)
        {
            return Results.Json(new { error = "invalid_client" }, statusCode: StatusCodes.Status401Unauthorized);
        }
        if (form["grant_type"] != "client_credentials")
        {
            return Results.BadRequest(new { error = "unsupported_grant_type" });
        }

        var lifetime = mode == ShortToken ? 5 : 3600;
        var token = store.IssueToken(TimeSpan.FromSeconds(lifetime));
        return Results.Ok(new { access_token = token, expires_in = lifetime, token_type = "Bearer" });
    }

    private static IResult ListEvents(string? from, string? to, string? modifiedSince, HttpRequest request, ScenarioState scenario, CalendarStore store)
    {
        if (Authenticate(request, scenario, store) is { } denied) return denied;

        var start = DateTime.MinValue;
        var end = DateTime.MaxValue;
        DateTime? since = null;
        if (from is not null && !Clock.TryParseIso(from, out start)) return InvalidQuery("from");
        if (to is not null && !Clock.TryParseIso(to, out end)) return InvalidQuery("to");
        if (modifiedSince is not null)
        {
            if (!Clock.TryParseIso(modifiedSince, out var parsed)) return InvalidQuery("modifiedSince");
            since = parsed;
        }

        var events = store.All()
            .Where(e => e.StartUtc < end && e.EndUtc > start)
            .Where(e => since is null || e.ModifiedUtc > since)
            .OrderBy(e => e.StartUtc).ThenBy(e => e.ExternalId, StringComparer.Ordinal)
            .ToList();
        DateTime? cursor = events.Count > 0 ? events.Max(e => e.ModifiedUtc) : since;
        return Results.Ok(new { events, cursor });
    }

    private static IResult Reserve(ReservationRequest body, HttpRequest request, ScenarioState scenario, CalendarStore store)
    {
        if (Authenticate(request, scenario, store) is { } denied) return denied;

        if (string.IsNullOrWhiteSpace(body.ReservationId) || string.IsNullOrWhiteSpace(body.VenueCode)
            || body.StartUtc is null || body.EndUtc is null || body.EndUtc <= body.StartUtc)
        {
            return Results.BadRequest(new { error = "invalid_request", detail = "reservationId, venueCode, startUtc and endUtc (after startUtc) are required." });
        }

        var hold = new HoldRequest(
            body.ReservationId.Trim(),
            body.VenueCode.Trim().ToUpperInvariant(),
            Clock.Truncate(body.StartUtc.Value.UtcDateTime),
            Clock.Truncate(body.EndUtc.Value.UtcDateTime),
            string.IsNullOrWhiteSpace(body.Title) ? "Reservation hold" : body.Title.Trim());

        var outcome = store.Reserve(hold, forceConflict: scenario.Mode == Conflict);
        if (outcome.Conflict is { } clash)
        {
            return Results.Json(new
            {
                error = "conflict",
                conflictingEvent = new { clash.ExternalId, clash.Title, clash.StartUtc, clash.EndUtc },
                alternatives = outcome.Alternatives,
            }, statusCode: StatusCodes.Status409Conflict);
        }

        var result = new { externalId = outcome.Hold!.ExternalId, reservationId = hold.ReservationId };
        return outcome.Created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
    }

    private static IResult Release(string reservationId, HttpRequest request, ScenarioState scenario, CalendarStore store)
    {
        if (Authenticate(request, scenario, store) is { } denied) return denied;
        store.Release(reservationId.Trim());
        return Results.NoContent();
    }

    /// <summary>Returns a 401 result when the bearer token is missing, unknown or expired (or the one-shot mode fires).</summary>
    private static IResult? Authenticate(HttpRequest request, ScenarioState scenario, CalendarStore store)
    {
        var header = request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        if (scenario.TryConsume(UnauthorizedOnce) || !store.IsValidToken(token))
        {
            return Results.Json(new { error = "invalid_token" }, statusCode: StatusCodes.Status401Unauthorized);
        }
        return null;
    }

    private static IResult InvalidQuery(string name) =>
        Results.BadRequest(new { error = "invalid_query", parameter = name, expected = "ISO-8601 instant" });

    private static bool TryDecodeBasic(string? parameter, out string? id, out string? secret)
    {
        id = secret = null;
        if (string.IsNullOrEmpty(parameter)) return false;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(parameter));
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) return false;
            id = Uri.UnescapeDataString(text[..colon]);
            secret = Uri.UnescapeDataString(text[(colon + 1)..]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record ReservationRequest(string? ReservationId, string? VenueCode, DateTimeOffset? StartUtc, DateTimeOffset? EndUtc, string? Title);

public sealed record HoldRequest(string ReservationId, string VenueCode, DateTime StartUtc, DateTime EndUtc, string Title);

public sealed record CalendarEvent(string ExternalId, string VenueCode, string Title, DateTime StartUtc, DateTime EndUtc, DateTime ModifiedUtc, string? ReservationId);

public sealed record ReservationOutcome(CalendarEvent? Hold, bool Created, CalendarEvent? Conflict, IReadOnlyList<DateTime> Alternatives);

/// <summary>Seed events (materialised once at startup), holds and issued tokens.</summary>
public sealed class CalendarStore : IResettable
{
    private const int AlternativeCount = 3;
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly ReferenceData _data;
    private readonly IReadOnlyList<CalendarEvent> _seed;
    private readonly Dictionary<string, CalendarEvent> _holds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _tokens = new(StringComparer.Ordinal);

    public CalendarStore(ReferenceData data, MockStartup startup)
    {
        _data = data;
        var today = DateOnly.FromDateTime(startup.StartedAtUtc);
        _seed = data.CalendarSeeds.Select(s =>
        {
            var zone = data.ZoneFor(s.VenueCode);
            var start = ReferenceData.LocalToUtc(today.AddDays(s.DayOffset), TimeOnly.ParseExact(s.LocalStart, "HH:mm", CultureInfo.InvariantCulture), zone);
            return new CalendarEvent(s.ExternalId, s.VenueCode, s.Title, start, start.AddMinutes(s.DurationMinutes), startup.StartedAtUtc, null);
        }).ToList();
    }

    public string IssueToken(TimeSpan lifetime)
    {
        var now = DateTime.UtcNow;
        foreach (var expired in _tokens.Where(t => t.Value <= now).Select(t => t.Key).ToList()) _tokens.TryRemove(expired, out _);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _tokens[token] = now + lifetime;
        return token;
    }

    public bool IsValidToken(string? token) =>
        !string.IsNullOrEmpty(token) && _tokens.TryGetValue(token, out var expires) && expires > DateTime.UtcNow;

    public IReadOnlyList<CalendarEvent> All()
    {
        lock (_gate) return [.. _seed, .. _holds.Values];
    }

    public ReservationOutcome Reserve(HoldRequest request, bool forceConflict)
    {
        lock (_gate)
        {
            var others = _seed.Concat(_holds.Values)
                .Where(e => string.Equals(e.VenueCode, request.VenueCode, StringComparison.OrdinalIgnoreCase) && e.ReservationId != request.ReservationId)
                .OrderBy(e => e.StartUtc)
                .ToList();

            var clash = forceConflict
                ? others.FirstOrDefault()
                : others.FirstOrDefault(e => e.StartUtc < request.EndUtc && e.EndUtc > request.StartUtc);
            if (clash is not null)
            {
                return new ReservationOutcome(null, false, clash, Alternatives(request, others));
            }

            var created = !_holds.TryGetValue(request.ReservationId, out var existing);
            var hold = new CalendarEvent(
                existing?.ExternalId ?? "HOLD-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                request.VenueCode, request.Title, request.StartUtc, request.EndUtc, Clock.UtcNow(), request.ReservationId);
            _holds[request.ReservationId] = hold;
            return new ReservationOutcome(hold, created, null, []);
        }
    }

    public void Release(string reservationId)
    {
        lock (_gate) _holds.Remove(reservationId);
    }

    public void Reset()
    {
        lock (_gate) _holds.Clear();
        _tokens.Clear();
    }

    /// <summary>Same-duration starts on the same venue-local day, on 30-minute boundaries, nearest first, free of overlaps.</summary>
    private List<DateTime> Alternatives(HoldRequest request, List<CalendarEvent> others)
    {
        var zone = _data.ZoneFor(request.VenueCode);
        var duration = request.EndUtc - request.StartUtc;
        var day = ReferenceData.LocalDate(request.StartUtc, zone);
        var candidates = new List<DateTime>();
        for (var local = TimeSpan.Zero; local + duration <= TimeSpan.FromDays(1); local += Step)
        {
            var start = ReferenceData.LocalToUtc(day, TimeOnly.FromTimeSpan(local), zone);
            var end = start + duration;
            if (start == request.StartUtc || ReferenceData.LocalDate(start, zone) != day) continue;
            if (others.Any(e => e.StartUtc < end && e.EndUtc > start)) continue;
            candidates.Add(start);
        }
        return candidates
            .Distinct()
            .OrderBy(c => Math.Abs((c - request.StartUtc).Ticks))
            .ThenBy(c => c)
            .Take(AlternativeCount)
            .ToList();
    }
}
