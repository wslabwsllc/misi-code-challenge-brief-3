namespace EventPermits.Mocks;

/// <summary>Fictional geocoder: exact (case-insensitive, trimmed) address match against geocode.json.</summary>
public sealed class GeocodeService : IMockService
{
    public const string Ambiguous = "ambiguous";
    public const string NoMatch = "no_match";

    public string Name => "geocode";

    public IReadOnlyList<string> Modes { get; } = [.. EventPermits.Mocks.Modes.Common, Ambiguous, NoMatch];

    public void ConfigureServices(IServiceCollection services, MockOptions options)
    {
    }

    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/geocode", Geocode)
            .AsServiceEndpoint()
            .WithName("Geocode")
            .WithSummary("Geocode an address. Unknown addresses return an empty result list.");
    }

    private static IResult Geocode(string? q, ScenarioState scenario, ReferenceData data)
    {
        if (string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "missing_query" });

        var mode = scenario.Mode;
        if (mode == NoMatch || !data.Geocode.TryGetValue(q.Trim(), out var entry))
        {
            return Results.Ok(new { results = Array.Empty<GeocodeCandidate>() });
        }

        if (mode == Ambiguous)
        {
            // Two equally likely candidates near the best known one. An entry with no candidates stays empty.
            if (entry.Candidates.Count == 0) return Results.Ok(new { results = Array.Empty<GeocodeCandidate>() });
            var first = entry.Candidates[0];
            return Results.Ok(new
            {
                results = new[]
                {
                    first with { Confidence = 0.8 },
                    first with { Label = first.Label + " (alternate entrance)", Latitude = Math.Round(first.Latitude + 0.003, 6), Longitude = Math.Round(first.Longitude + 0.003, 6), Confidence = 0.8 },
                },
            });
        }

        return Results.Ok(new { results = entry.Candidates });
    }
}
