namespace EventPermits.Mocks;

/// <summary>Fictional applicant registry: REST lookup protected by an API key.</summary>
public sealed class RegistryService : IMockService
{
    public const string Unauthorized = "unauthorized";
    public const string NotFound = "notfound";
    public const string RateLimited = "ratelimited";

    public string Name => "registry";

    public IReadOnlyList<string> Modes { get; } = [.. EventPermits.Mocks.Modes.Common, Unauthorized, NotFound, RateLimited];

    public void ConfigureServices(IServiceCollection services, MockOptions options)
    {
    }

    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/applicants/{reference}", Lookup)
            .AsServiceEndpoint()
            .WithName("GetApplicant")
            .WithSummary("Look up an applicant by registry reference. Requires X-API-Key.");
    }

    private static IResult Lookup(string reference, HttpRequest request, ScenarioState scenario, MockOptions options, ReferenceData data)
    {
        switch (scenario.Mode)
        {
            case RateLimited:
                request.HttpContext.Response.Headers.RetryAfter = "2";
                return Results.Json(new { error = "rate_limited" }, statusCode: StatusCodes.Status429TooManyRequests);
            case Unauthorized:
                return InvalidApiKey();
        }

        if (!Secrets.Matches(request.Headers["X-API-Key"], options.Registry.ApiKey)) return InvalidApiKey();

        if (scenario.Mode == NotFound || !data.Applicants.TryGetValue(reference.Trim(), out var applicant))
        {
            return Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new
        {
            referenceId = applicant.ReferenceId,
            matchedName = applicant.MatchedName,
            eligibilityStatus = applicant.EligibilityStatus,
            verifiedAt = Clock.UtcNow(),
        });
    }

    private static IResult InvalidApiKey() =>
        Results.Json(new { error = "invalid_api_key" }, statusCode: StatusCodes.Status401Unauthorized);
}
