namespace EventPermits.Mocks;

/// <summary>Requires X-Mock-Admin-Key = Mock:AdminKey. 503 when no admin key is configured.</summary>
public sealed class AdminKeyFilter(MockOptions options) : IEndpointFilter
{
    public const string Header = "X-Mock-Admin-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (string.IsNullOrEmpty(options.AdminKey))
        {
            return Results.Json(new { error = "admin_key_not_configured" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (!Secrets.Matches(context.HttpContext.Request.Headers[Header], options.AdminKey))
        {
            return Results.Json(new { error = "invalid_admin_key" }, statusCode: StatusCodes.Status401Unauthorized);
        }
        return await next(context);
    }
}

public sealed record ScenarioUpdate(string? Mode, int? LatencyMs);

/// <summary>Health and the admin API shared by every service.</summary>
public static class Admin
{
    public const int MaxLatencyMs = 60_000;

    public static RouteGroupBuilder MapAdminGroup(this IEndpointRouteBuilder app) =>
        app.MapGroup("/_admin").AddEndpointFilter<AdminKeyFilter>();

    public static void MapCommonEndpoints(this WebApplication app)
    {
        app.MapGet("/health", (ScenarioState s) => Results.Ok(new { status = "Healthy", service = s.Service, mode = s.Mode }))
            .WithName("Health")
            .WithSummary("Process health. Always 200; reports the process, not the scenario.");

        var admin = app.MapAdminGroup().ExcludeFromDescription();

        admin.MapGet("/scenario", (ScenarioState s) => Results.Ok(s.Snapshot()));

        admin.MapPut("/scenario", (ScenarioUpdate update, ScenarioState s, ILogger<ScenarioState> log) =>
        {
            var mode = update.Mode?.Trim().ToLowerInvariant();
            if (mode is not null && !s.IsAvailable(mode))
            {
                return Results.BadRequest(new { error = "unknown_mode", availableModes = s.AvailableModes });
            }
            if (update.LatencyMs is < 0 or > MaxLatencyMs)
            {
                return Results.BadRequest(new { error = "invalid_latency", maxLatencyMs = MaxLatencyMs });
            }
            s.Set(mode, update.LatencyMs);
            var snapshot = s.Snapshot();
            log.LogWarning("Scenario changed to mode {Mode} latency {LatencyMs} ms", snapshot.Mode, snapshot.LatencyMs);
            return Results.Ok(snapshot);
        });

        admin.MapGet("/stats", (ScenarioState s, RequestStats stats) => Results.Ok(stats.Snapshot(s.Service)));

        admin.MapPost("/reset", (ScenarioState s, RequestStats stats, IEnumerable<IResettable> states, ILogger<ScenarioState> log) =>
        {
            s.Reset();
            stats.Reset();
            foreach (var state in states) state.Reset();
            log.LogWarning("Mock reset to mode {Mode}", s.Mode);
            return Results.Ok(s.Snapshot());
        });
    }
}
