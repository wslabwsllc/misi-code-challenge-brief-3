using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;

namespace EventPermits.Mocks;

/// <summary>Time helpers. Times are truncated to whole milliseconds so they round-trip through ISO-8601 text exactly.</summary>
public static class Clock
{
    public static DateTime UtcNow() => Truncate(DateTime.UtcNow);

    public static DateTime Truncate(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    public static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Parses an ISO-8601 instant; values without an offset are treated as UTC.</summary>
    public static bool TryParseIso(string? text, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        // An unencoded '+' in a query string arrives as a space.
        var normalized = text.Trim().Replace(' ', '+');
        if (!DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return false;
        utc = parsed.UtcDateTime;
        return true;
    }
}

/// <summary>Constant-time secret comparison. Both sides are hashed first so lengths are not leaked either.</summary>
public static class Secrets
{
    public static bool Matches(StringValues provided, string? expected)
    {
        if (string.IsNullOrEmpty(expected) || provided.Count != 1 || string.IsNullOrEmpty(provided[0])) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(provided[0]!));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public static class Correlation
{
    public const string Header = "X-Correlation-ID";

    /// <summary>Returns the incoming correlation id if it is short and made of safe characters, else null.</summary>
    public static string? Read(HttpRequest request)
    {
        var value = request.Headers[Header].ToString().Trim();
        if (value.Length is 0 or > 128) return null;
        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')) return null;
        }
        return value;
    }
}

/// <summary>Echoes X-Correlation-ID and writes one structured log line per request (never headers or bodies).</summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger, ScenarioState scenario)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var correlationId = Correlation.Read(context.Request);
        if (correlationId is not null)
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[Correlation.Header] = correlationId;
                return Task.CompletedTask;
            });
        }

        try
        {
            await next(context);
        }
        finally
        {
            var elapsedMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
            logger.LogInformation(
                "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs} ms (aborted: {Aborted}) correlation {CorrelationId} service {Service}",
                context.Request.Method,
                context.Request.Path.Value,
                context.Response.StatusCode,
                elapsedMs,
                context.RequestAborted.IsCancellationRequested,
                correlationId ?? "",
                scenario.Service);
        }
    }
}

/// <summary>`probe &lt;url&gt;`: exit 0 when the URL answers 2xx within 5 s. Used by container health checks.</summary>
public static class Probe
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2 || !Uri.TryCreate(args[1], UriKind.Absolute, out var uri))
        {
            await Console.Error.WriteLineAsync("usage: probe <url>");
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await http.GetAsync(uri);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"probe failed: {ex.GetType().Name}");
            return 1;
        }
    }
}
