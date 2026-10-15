using System.Security.Claims;
using System.Text.Json;
using EventPermits.Web.Api;
using EventPermits.Web.Components;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;

namespace EventPermits.Web.Hosting;

public static class Pipeline
{
    public static void UseEventPermits(this WebApplication app)
    {
        app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Routing.RouteHandlerOptions>>().Value.ThrowOnBadRequest = true;

        // Behind a TLS-terminating load balancer: restore client scheme/address first so logs, cookies and redirects see HTTPS.
        if (app.Services.GetRequiredService<HostingOptions>().BehindTlsProxy)
        {
            app.UseForwardedHeaders();
            app.UseHsts(); // no UseHttpsRedirection: the load balancer redirects and the container listens on HTTP only
        }

        app.UseSerilogRequestLogging(o =>
        {
            o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";
            o.Logger = app.Services.GetRequiredService<Serilog.ILogger>();
            o.EnrichDiagnosticContext = (diag, http) =>
            {
                diag.Set("CorrelationId", http.TraceIdentifier);
                diag.Set("Actor", http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous");
            };
            o.GetLevel = (http, _, ex) => ex is not null || http.Response.StatusCode >= 500
                ? Serilog.Events.LogEventLevel.Error
                : http.Request.Path.StartsWithSegments("/health") || http.Request.Path.StartsWithSegments("/_blazor") || http.Request.Path.StartsWithSegments("/_framework")
                    ? Serilog.Events.LogEventLevel.Verbose
                    : Serilog.Events.LogEventLevel.Information;
        });

        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandlingPath = "/error",
            CreateScopeForErrors = true,
            AllowStatusCode404Response = true,
        });
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/health")
                           && !ctx.Request.Path.StartsWithSegments("/webhooks") && !ctx.Request.Path.StartsWithSegments("/files"),
            b => b.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
        app.UseAuthentication();
        app.UseMiddleware<CorrelationMiddleware>();
        app.UseAuthorization();
        app.UseAntiforgery();

        MapHealth(app);
        app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
        app.MapApi();
        app.MapAccountEndpoints();
        app.MapFileEndpoints();
        app.MapWebhookEndpoints();
        app.MapEventDayEndpoints();

        app.MapStaticAssets();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
    }

    /// <summary>Minimal pipeline of the background worker: health only (container probe), no UI.</summary>
    public static void UseWorkerEndpoints(this WebApplication app) => MapHealth(app);

    private static void MapHealth(WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains("live"), ResponseWriter = WriteHealth }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"), ResponseWriter = WriteHealth }).AllowAnonymous();
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            // Degraded dependencies still answer 200: the process and database are serving (FR-16).
            ResultStatusCodes = { [HealthStatus.Healthy] = 200, [HealthStatus.Degraded] = 200, [HealthStatus.Unhealthy] = 503 },
            ResponseWriter = WriteHealth,
        }).AllowAnonymous();
    }

    private static Task WriteHealth(HttpContext http, HealthReport report)
    {
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            totalDurationMs = (int)report.TotalDuration.TotalMilliseconds,
            components = report.Entries.ToDictionary(e => e.Key, e => new
            {
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = (int)e.Value.Duration.TotalMilliseconds,
            }),
        }));
    }
}
