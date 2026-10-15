using EventPermits.Application;
using EventPermits.Application.Services;
using EventPermits.Domain;

namespace EventPermits.Web.Api;

/// <summary>
/// Read endpoints behind the event-day and messaging pages (13, 14, 15, 12, 22). The commands those pages use are in
/// <see cref="ApiEndpoints"/>; these expose the same queries the pages call, with the same service-side authorization.
/// </summary>
public static class EventDayEndpoints
{
    public static void MapEventDayEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter<AntiforgeryFilter>();
        static async Task<Actor> Me(HttpContext h, ActorResolver a, CancellationToken ct) => await a.ResolveAsync(h.User, ct);

        api.MapGet("/operations/cemeteries", async (HttpContext h, ActorResolver a, OperationsService s, CancellationToken ct) =>
            Results.Ok(await s.CemeteriesAsync(await Me(h, a, ct), ct)));
        api.MapGet("/operations/events/{id:guid}", async (Guid id, HttpContext h, ActorResolver a, OperationsService s, CancellationToken ct) =>
            Results.Ok(await s.EventDayAsync(await Me(h, a, ct), id, ct)));
        api.MapGet("/operations/closure-queue", async (HttpContext h, ActorResolver a, OperationsService s, CancellationToken ct) =>
            Results.Ok(await s.ClosureQueueAsync(await Me(h, a, ct), ct)));
        api.MapGet("/operations/run-of-show.pdf", async (DateOnly date, Guid? cemeteryId, HttpContext h, ActorResolver a, OperationsService s, CancellationToken ct) =>
            Results.File(await s.RunOfShowAsync(await Me(h, a, ct), date, cemeteryId, ct), "application/pdf", $"run-of-show-{date:yyyy-MM-dd}.pdf"));

        api.MapGet("/incidents/targets", async (HttpContext h, ActorResolver a, IncidentService s, CancellationToken ct) =>
            Results.Ok(await s.TargetsAsync(await Me(h, a, ct), ct)));
        api.MapGet("/incidents/{id:guid}", async (Guid id, HttpContext h, ActorResolver a, IncidentService s, CancellationToken ct) =>
        {
            var (incident, photos, history, reference) = await s.GetAsync(await Me(h, a, ct), id, ct);
            return Results.Ok(new { incident, photos, history, caseReference = reference });
        });

        api.MapGet("/notifications/{id:guid}", async (Guid id, HttpContext h, ActorResolver a, NotificationService s, CancellationToken ct) =>
            Results.Ok(await s.GetAsync(await Me(h, a, ct), id, ct)));
        api.MapGet("/notifications/templates", async (HttpContext h, ActorResolver a, NotificationService s, CancellationToken ct) =>
            Results.Ok(await s.TemplatesAsync(await Me(h, a, ct), ct)));
        api.MapGet("/notifications/counts", async (HttpContext h, ActorResolver a, NotificationService s, CancellationToken ct) =>
            Results.Ok(await s.StatusCountsAsync(await Me(h, a, ct), ct)));
        api.MapGet("/messaging/monitor", async (HttpContext h, ActorResolver a, NotificationService s, CancellationToken ct) =>
            Results.Ok(await s.MonitorAsync(await Me(h, a, ct), ct)));
        api.MapGet("/messaging/queue", async (HttpContext h, ActorResolver a, NotificationService s, CancellationToken ct) =>
            Results.Ok(await s.QueueAsync(await Me(h, a, ct), ct)));
        api.MapGet("/messaging/receipts", async (WebhookOutcome? outcome, int? page, HttpContext h, ActorResolver a, NotificationService s, CancellationToken ct) =>
        {
            var (rows, total) = await s.ReceiptsAsync(await Me(h, a, ct), outcome, page ?? 1, ct);
            return Results.Ok(new { rows, total });
        });
    }
}
