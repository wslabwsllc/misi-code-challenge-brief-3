using System.Text;
using EventPermits.Application.Services;

namespace EventPermits.Web.Api;

public static class WebhookEndpoints
{
    /// <summary>
    /// Delivery callbacks from the Notification Gateway (brief §7.1). Anonymous but HMAC-verified over the raw body;
    /// exempt from antiforgery (machine-to-machine); body size bounded. 401 for bad signature or stale timestamp,
    /// 200 for accepted and duplicate events.
    /// </summary>
    public static void MapWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/webhooks/delivery", async (HttpContext http, NotificationService notifications, CancellationToken ct) =>
        {
            if (http.Request.ContentLength is > 16 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var raw = await reader.ReadToEndAsync(ct);
            if (raw.Length > 16 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var result = await notifications.ProcessDeliveryAsync(raw, http.Request.Headers["X-Timestamp"].FirstOrDefault(), http.Request.Headers["X-Signature"].FirstOrDefault(), ct);
            return Results.Json(new { outcome = result.Outcome.ToString(), result.Reason, correlationId = http.TraceIdentifier }, statusCode: result.StatusCode);
        }).AllowAnonymous().DisableAntiforgery();
    }
}
