using EventPermits.Application;
using EventPermits.Application.Services;

namespace EventPermits.Web.Api;

/// <summary>Read queries used by pages 07, 08, 09 and 21, and category creation for page 17, over the same service methods the pages call.</summary>
public static partial class ApiEndpoints
{
    private static void MapSchedulingPageEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/schedule/slots", async (Guid venueId, DateOnly date, int? durationMinutes, int? stepMinutes, HttpContext h, ActorResolver a, SchedulingService s, CancellationToken ct) =>
            Results.Ok(await s.DaySlotsAsync(await Me(h, a, ct), venueId, date, durationMinutes ?? 60, stepMinutes ?? 60, ct)));
        api.MapGet("/schedule/day", async (Guid venueId, DateOnly date, HttpContext h, ActorResolver a, SchedulingService s, CancellationToken ct) =>
            Results.Ok(await s.DayAsync(await Me(h, a, ct), venueId, date, ct)));
        api.MapGet("/schedule/desk", async (HttpContext h, ActorResolver a, SchedulingService s, CancellationToken ct) =>
            Results.Ok(await s.DeskAsync(await Me(h, a, ct), ct)));
        api.MapGet("/resources/planner", async (Guid cemeteryId, DateOnly date, HttpContext h, ActorResolver a, SchedulingService s, CancellationToken ct) =>
        {
            var me = await Me(h, a, ct);
            return Results.Ok(new { lines = await s.ResourcesAsync(me, cemeteryId, date, ct), reservations = await s.PlannerAsync(me, cemeteryId, date, ct) });
        });
        api.MapGet("/calendar/overview", async (HttpContext h, ActorResolver a, CalendarSyncService s, CancellationToken ct) =>
            Results.Ok(await s.OverviewAsync(await Me(h, a, ct), ct)));
        api.MapPost("/admin/categories", async (CategoryBody b, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
        {
            await s.SaveCategoryAsync(await Me(h, a, ct), null, null, b.Code, b.Name, b.Description, b.RequiredDocuments ?? [], b.ReviewChecklist ?? [], b.IsActive, ct);
            return Results.NoContent();
        });
    }
}
