using System.Security.Claims;
using EventPermits.Application;
using Serilog.Context;

namespace EventPermits.Web.Hosting;

/// <summary>
/// Correlation ID for logs, audit, integration attempts and user-visible error references (FR-16).
/// HTTP requests take it from X-Correlation-ID when valid; Blazor actions start a new one per user operation.
/// </summary>
public sealed class CorrelationContext : ICorrelationContext
{
    public const string Header = "X-Correlation-ID";
    private string? _current;

    public string CorrelationId => _current ??= Set(NewId());

    public string Set(string id)
    {
        _current = id;
        AmbientCorrelation.Current = id;
        return id;
    }

    public string BeginOperation() => Set(NewId());

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Bounded, safe characters only, so a caller cannot inject log content.</summary>
    public static bool IsValid(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

public sealed class CorrelationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CorrelationContext correlation)
    {
        var incoming = context.Request.Headers[CorrelationContext.Header].ToString();
        var id = CorrelationContext.IsValid(incoming) ? incoming : CorrelationContext.NewId();
        correlation.Set(id);
        context.TraceIdentifier = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationContext.Header] = id;
            return Task.CompletedTask;
        });
        using (LogContext.PushProperty("CorrelationId", id))
        using (LogContext.PushProperty("Actor", context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous"))
        {
            await next(context);
        }
    }
}

/// <summary>Baseline response headers (E13-02). HSTS is added separately behind a TLS proxy.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, ContentSecurityPolicy csp)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "SAMEORIGIN";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            h.ContentSecurityPolicy = csp.Value;
            return Task.CompletedTask;
        });
        return next(context);
    }
}

/// <summary>
/// No third-party sources. Blazor Server needs its own script and a WebSocket to the same origin. Download
/// redirects and previews land on the object store's origin (A-16), so that origin is allowed for images and frames.
/// </summary>
public sealed class ContentSecurityPolicy(string storageOrigin)
{
    public string Value { get; } =
        $"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: {storageOrigin}; " +
        $"frame-src 'self' {storageOrigin}; object-src 'none'; connect-src 'self' ws: wss:; font-src 'self'; " +
        "frame-ancestors 'self'; base-uri 'self'; form-action 'self'";

    public static string OriginOf(string? publicEndpoint, string? serviceUrl)
    {
        var endpoint = !string.IsNullOrWhiteSpace(publicEndpoint) ? publicEndpoint : serviceUrl;
        if (string.IsNullOrWhiteSpace(endpoint)) return "https://*.amazonaws.com";
        var uri = new Uri(endpoint);
        return uri.GetLeftPart(UriPartial.Authority);
    }
}
