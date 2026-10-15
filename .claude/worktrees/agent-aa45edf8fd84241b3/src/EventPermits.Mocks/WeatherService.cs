using System.Globalization;

namespace EventPermits.Mocks;

/// <summary>Fictional read-only forecast service, deterministic from weather.json.</summary>
public sealed class WeatherService : IMockService
{
    public const string RateLimited = "ratelimited";
    public const string Severe = "severe";

    public string Name => "weather";

    public IReadOnlyList<string> Modes { get; } = [.. EventPermits.Mocks.Modes.Common, RateLimited, Severe];

    public void ConfigureServices(IServiceCollection services, MockOptions options)
    {
    }

    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/forecast", Forecast)
            .AsServiceEndpoint()
            .WithName("GetForecast")
            .WithSummary("Daily forecast for a venue (venue code) and venue-local date (yyyy-MM-dd).");
    }

    private static IResult Forecast(string? venue, string? date, HttpResponse response, ScenarioState scenario, ReferenceData data)
    {
        var mode = scenario.Mode;
        if (mode == RateLimited)
        {
            response.Headers.RetryAfter = "30";
            return Results.Json(new { error = "rate_limited" }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (string.IsNullOrWhiteSpace(venue)) return Results.BadRequest(new { error = "missing_venue" });
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return Results.BadRequest(new { error = "invalid_date", expected = "yyyy-MM-dd" });
        }
        if (!data.Weather.TryGetValue(venue.Trim(), out var baseline))
        {
            return Results.Json(new { error = "unknown_venue" }, statusCode: StatusCodes.Status404NotFound);
        }

        var now = DateTime.UtcNow;
        var today = ReferenceData.LocalDate(now, data.ZoneFor(baseline.VenueCode));
        var offset = day.DayNumber - today.DayNumber;

        var advisory = mode == Severe || baseline.SevereDays.Contains(offset) ? "Severe"
            : baseline.AdvisoryDays.Contains(offset) ? "Advisory"
            : "None";

        // Small deterministic variation by day offset.
        var temperature = baseline.BaseTemperatureC + Mod(offset * 7, 5) - 2;
        var wind = baseline.BaseWindKph + Mod(offset * 3, 4) * 2;
        var (summary, precipitation) = advisory switch
        {
            "Severe" => ("Thunderstorms with damaging winds", 90),
            "Advisory" => ("Gusty winds and passing showers", 60),
            _ => (Mod(offset, 2) == 0 ? "Partly cloudy" : "Clear skies", 5 + Mod(offset * 13, 25)),
        };
        if (advisory == "Severe") wind += 45;
        else if (advisory == "Advisory") wind += 20;

        return Results.Ok(new
        {
            venueCode = baseline.VenueCode,
            date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            summary,
            temperatureC = temperature,
            windKph = wind,
            precipitationPct = precipitation,
            advisory,
            sourceTimestampUtc = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc),
        });
    }

    private static int Mod(int value, int n) => ((value % n) + n) % n;
}
