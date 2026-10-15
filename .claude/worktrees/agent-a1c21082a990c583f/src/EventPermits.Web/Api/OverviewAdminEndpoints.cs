using EventPermits.Application;
using EventPermits.Application.Services;

namespace EventPermits.Web.Api;

public sealed record LockBody(bool Locked, string? Reason);

/// <summary>Endpoints added with pages 01, 16, 18, 19, 24 and 25: account lock/unlock, audit filter choices, weather overview.</summary>
public static class OverviewAdminEndpoints
{
    public static RouteGroupBuilder MapOverviewAdminEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/admin/users/{id}/lock", async (string id, LockBody b, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
        {
            await s.SetLockAsync(await a.ResolveAsync(h.User, ct), id, b.Locked, b.Reason, ct);
            return Results.NoContent();
        });
        api.MapGet("/audit/facets", async (HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
            Results.Ok(await s.AuditFacetsAsync(await a.ResolveAsync(h.User, ct), ct)));
        api.MapGet("/weather/upcoming", async (int? days, HttpContext h, ActorResolver a, WeatherQueryService s, CancellationToken ct) =>
            Results.Ok(await s.UpcomingAsync(await a.ResolveAsync(h.User, ct), days ?? 14, ct)));
        api.MapGet("/weather/cache", async (HttpContext h, ActorResolver a, WeatherQueryService s, CancellationToken ct) =>
            Results.Ok(await s.CacheAsync(await a.ResolveAsync(h.User, ct), ct)));
        return api;
    }
}
