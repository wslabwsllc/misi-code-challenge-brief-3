using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using EventPermits.Application;
using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;

namespace EventPermits.Infrastructure.Integrations;

/// <summary>Per-request metadata carried through the handler chain (brief §7, plan §4.5).</summary>
public static class Call
{
    public static readonly HttpRequestOptionsKey<IntegrationKind> Kind = new("ep.kind");
    public static readonly HttpRequestOptionsKey<string> Operation = new("ep.operation");
    public static readonly HttpRequestOptionsKey<Guid?> ApplicationId = new("ep.application");
    public static readonly HttpRequestOptionsKey<int> AttemptCounter = new("ep.attempts");
    public static readonly HttpRequestOptionsKey<Guid> LastAttemptId = new("ep.lastAttempt");

    public static HttpRequestMessage Request(HttpMethod method, string uri, IntegrationKind kind, string operation, Guid? applicationId = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Options.Set(Kind, kind);
        request.Options.Set(Operation, operation);
        request.Options.Set(ApplicationId, applicationId);
        return request;
    }

    public static Guid? AttemptId(HttpRequestMessage request) =>
        request.Options.TryGetValue(LastAttemptId, out var id) ? id : null;
}

/// <summary>Adds the correlation ID of the current request or job to every outbound call (FR-16).</summary>
public sealed class CorrelationHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var correlation = AmbientCorrelation.Current ?? Guid.NewGuid().ToString("N");
        request.Headers.Remove("X-Correlation-ID");
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlation);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Innermost handler: records one sanitised <see cref="IntegrationAttempt"/> per HTTP attempt, retries included,
/// in its own context so the evidence survives even when the business transaction does not.
/// </summary>
public sealed partial class AttemptRecorderHandler(IDbContextFactory<AppDbContext> factory, TimeProvider clock, ILogger<AttemptRecorderHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attemptNumber = (request.Options.TryGetValue(Call.AttemptCounter, out var n) ? n : 0) + 1;
        request.Options.Set(Call.AttemptCounter, attemptNumber);
        var started = Stopwatch.GetTimestamp();
        var attempt = new IntegrationAttempt
        {
            At = clock.GetUtcNow(),
            Integration = request.Options.TryGetValue(Call.Kind, out var kind) ? kind : IntegrationKind.Registry,
            Operation = request.Options.TryGetValue(Call.Operation, out var op) ? op : "call",
            ApplicationId = request.Options.TryGetValue(Call.ApplicationId, out var app) ? app : null,
            RequestId = Guid.NewGuid().ToString("N")[..16],
            CorrelationId = request.Headers.TryGetValues("X-Correlation-ID", out var c) ? c.FirstOrDefault() ?? "" : "",
            Method = request.Method.Method,
            Url = Redaction.Url(request.RequestUri),
            RequestSummary = Redaction.RequestSummary(request),
            AttemptNumber = attemptNumber,
        };
        request.Headers.Remove("X-Request-ID");
        request.Headers.TryAddWithoutValidation("X-Request-ID", attempt.RequestId);
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            attempt.StatusCode = (int)response.StatusCode;
            attempt.Outcome = Classify(response.StatusCode);
            attempt.ResponseExcerpt = await Redaction.ResponseExcerptAsync(response, cancellationToken);
            if (attempt.Outcome != AttemptOutcome.Success) attempt.Error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            attempt.Outcome = ex is HttpRequestException ? AttemptOutcome.Offline : AttemptOutcome.Timeout;
            attempt.Error = ex is HttpRequestException h ? $"Transport failure: {h.HttpRequestError}" : "Timed out or cancelled";
            throw;
        }
        finally
        {
            attempt.DurationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            request.Options.Set(Call.LastAttemptId, attempt.Id);
            await SaveAsync(attempt);
        }
    }

    public static AttemptOutcome Classify(HttpStatusCode status) => (int)status switch
    {
        >= 200 and < 300 => AttemptOutcome.Success,
        408 or 429 or >= 500 => AttemptOutcome.Transient,
        _ => AttemptOutcome.ClientError,
    };

    private async Task SaveAsync(IntegrationAttempt attempt)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
            db.IntegrationAttempts.Add(attempt);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Evidence must never break the business call; the structured log still carries the attempt.
            logger.LogWarning(ex, "Could not store integration attempt {RequestId} for {Integration}", attempt.RequestId, attempt.Integration);
        }
        logger.LogInformation("Integration {Integration} {Operation} attempt {Attempt} {Outcome} {StatusCode} in {DurationMs} ms",
            attempt.Integration, attempt.Operation, attempt.AttemptNumber, attempt.Outcome, attempt.StatusCode, attempt.DurationMs);
    }
}

/// <summary>Masks keys, tokens and secrets before anything is logged or stored (tested).</summary>
public static partial class Redaction
{
    private static readonly string[] SecretHeaders = ["Authorization", "X-API-Key", "X-Signature", "X-Mock-Admin-Key", "Cookie", "Set-Cookie"];
    private static readonly string[] SafeHeaders = ["Idempotency-Key", "X-Correlation-ID", "X-Request-ID", "Content-Type", "Retry-After"];

    public static string Url(Uri? uri)
    {
        if (uri is null) return "";
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0) return uri.GetLeftPart(UriPartial.Path);
        var parts = query.Split('&').Select(p =>
        {
            var key = p.Split('=')[0];
            return SensitiveName().IsMatch(key) ? key + "=***" : p;
        });
        return uri.GetLeftPart(UriPartial.Path) + "?" + string.Join('&', parts);
    }

    public static string RequestSummary(HttpRequestMessage request)
    {
        var sb = new StringBuilder();
        sb.Append(request.Method.Method).Append(' ').Append(Url(request.RequestUri));
        foreach (var header in request.Headers)
        {
            if (SecretHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) sb.Append($"\n{header.Key}: ***");
            else if (SafeHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) sb.Append($"\n{header.Key}: {string.Join(",", header.Value)}");
        }
        return sb.ToString();
    }

    public static async Task<string?> ResponseExcerptAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content is null) return null;
        try
        {
            await response.Content.LoadIntoBufferAsync(ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            var header = string.Join('\n', response.Headers
                .Where(h => SafeHeaders.Contains(h.Key, StringComparer.OrdinalIgnoreCase))
                .Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));
            var body = Body(text);
            return (header.Length > 0 ? header + "\n\n" : "") + (body.Length > 2048 ? body[..2048] + "…" : body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Masks JSON/form values of fields named like secret, password, token or key.</summary>
    public static string Body(string text) =>
        SensitiveJson().Replace(SensitiveForm().Replace(text, "$1=***"), "$1\"***\"");

    [GeneratedRegex(@"(?i)(key|token|secret|password|signature)")]
    private static partial Regex SensitiveName();

    [GeneratedRegex("(?i)(\"[A-Za-z_]*(?:token|secret|password|apikey|api_key)\"\\s*:\\s*)\"[^\"]*\"")]
    private static partial Regex SensitiveJson();

    [GeneratedRegex("(?i)\\b((?:client_secret|access_token|password))=[^&\\s]*")]
    private static partial Regex SensitiveForm();
}

/// <summary>Shared resilience: per-attempt timeout, transient-only retries with Retry-After, circuit breaker (brief §7.4).</summary>
public static class Resilience
{
    public static IHttpClientBuilder AddIntegrationPipeline(this IHttpClientBuilder builder, IntegrationKind kind, TimeSpan attemptTimeout, int retries = 2)
    {
        builder.AddHttpMessageHandler<CorrelationHandler>();
        builder.AddResilienceHandler($"{kind}-pipeline", pipeline =>
        {
            pipeline.AddTimeout(TimeSpan.FromSeconds(attemptTimeout.TotalSeconds * (retries + 1) + 6));
            if (retries > 0)
            {
                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = retries,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromMilliseconds(400),
                    MaxDelay = TimeSpan.FromSeconds(3),
                    UseJitter = true,
                    // Retry only transient failures: transport errors, timeouts, 408, 429 and 5xx. Never 404 or other 4xx.
                    ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome)),
                    DelayGenerator = args =>
                    {
                        var retryAfter = args.Outcome.Result?.Headers.RetryAfter?.Delta;
                        // Honour Retry-After within a bounded delay; longer waits are left to the user's retry.
                        return ValueTask.FromResult<TimeSpan?>(retryAfter is { } d && d <= TimeSpan.FromSeconds(3) ? d : null);
                    },
                });
            }
            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.8,
                MinimumThroughput = 8,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(15),
                ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome) && args.Outcome.Result?.StatusCode != HttpStatusCode.TooManyRequests),
            });
            pipeline.AddTimeout(attemptTimeout);
        });
        builder.AddHttpMessageHandler<AttemptRecorderHandler>();
        return builder;
    }

    public static bool IsTransient(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception is HttpRequestException or TimeoutException or Polly.Timeout.TimeoutRejectedException
        || outcome.Result is { } r && ((int)r.StatusCode >= 500 || r.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);

    /// <summary>Maps the end result of a call (after retries) to the application's result status.</summary>
    public static ExternalCallStatus StatusOf(HttpStatusCode code) => code switch
    {
        HttpStatusCode.NotFound => ExternalCallStatus.NotFound,
        HttpStatusCode.TooManyRequests => ExternalCallStatus.RateLimited,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ExternalCallStatus.Unauthorized,
        HttpStatusCode.Conflict => ExternalCallStatus.Conflict,
        >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout => ExternalCallStatus.Unavailable,
        _ => ExternalCallStatus.Rejected,
    };
}
