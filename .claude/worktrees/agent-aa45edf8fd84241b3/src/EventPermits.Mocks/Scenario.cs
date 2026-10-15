using Microsoft.AspNetCore.Routing.Patterns;

namespace EventPermits.Mocks;

/// <summary>Scenario mode names shared by every service.</summary>
public static class Modes
{
    public const string Normal = "normal";
    public const string Slow = "slow";
    public const string Timeout = "timeout";
    public const string Offline = "offline";
    public const string Http500 = "http500";

    public static readonly string[] Common = [Normal, Slow, Timeout, Offline, Http500];
}

/// <summary>A service that can run in this process.</summary>
public interface IMockService
{
    string Name { get; }

    /// <summary>Every mode this service accepts, common ones included.</summary>
    IReadOnlyList<string> Modes { get; }

    void ConfigureServices(IServiceCollection services, MockOptions options);

    void MapEndpoints(WebApplication app);
}

/// <summary>In-memory service state cleared by POST /_admin/reset.</summary>
public interface IResettable
{
    void Reset();
}

public sealed record ScenarioSnapshot(string Service, string Mode, int LatencyMs, IReadOnlyList<string> AvailableModes);

/// <summary>The current scenario mode and extra latency. Thread-safe singleton.</summary>
public sealed class ScenarioState
{
    private readonly object _gate = new();
    private string _mode;
    private int _latencyMs;

    public ScenarioState(string service, string initialMode, int initialLatencyMs, IReadOnlyList<string> availableModes)
    {
        Service = service;
        AvailableModes = availableModes;
        InitialMode = initialMode.Trim().ToLowerInvariant();
        if (!IsAvailable(InitialMode))
        {
            throw new ArgumentException($"Mode '{initialMode}' is not available for service '{service}'. Use one of: {string.Join(", ", availableModes)}.");
        }
        _mode = InitialMode;
        _latencyMs = Math.Max(0, initialLatencyMs);
    }

    public string Service { get; }
    public string InitialMode { get; }
    public IReadOnlyList<string> AvailableModes { get; }

    public string Mode
    {
        get { lock (_gate) return _mode; }
    }

    public bool IsAvailable(string mode) => AvailableModes.Contains(mode, StringComparer.Ordinal);

    public ScenarioSnapshot Snapshot()
    {
        lock (_gate) return new ScenarioSnapshot(Service, _mode, _latencyMs, AvailableModes);
    }

    public void Set(string? mode, int? latencyMs)
    {
        lock (_gate)
        {
            if (mode is not null) _mode = mode;
            if (latencyMs is not null) _latencyMs = latencyMs.Value;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _mode = InitialMode;
            _latencyMs = 0;
        }
    }

    /// <summary>One-shot modes: if the mode is active, switch back to normal and return true.</summary>
    public bool TryConsume(string mode)
    {
        lock (_gate)
        {
            if (_mode != mode) return false;
            _mode = Modes.Normal;
            return true;
        }
    }
}

/// <summary>Request counters for GET /_admin/stats.</summary>
public sealed class RequestStats
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _byEndpoint = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _byStatus = new(StringComparer.Ordinal);
    private int _total;
    private DateTime _since = Clock.UtcNow();

    public void Record(string endpoint, string status)
    {
        lock (_gate)
        {
            _total++;
            _byEndpoint[endpoint] = _byEndpoint.GetValueOrDefault(endpoint) + 1;
            _byStatus[status] = _byStatus.GetValueOrDefault(status) + 1;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _total = 0;
            _byEndpoint.Clear();
            _byStatus.Clear();
            _since = Clock.UtcNow();
        }
    }

    public object Snapshot(string service)
    {
        lock (_gate)
        {
            return new
            {
                service,
                total = _total,
                byEndpoint = new SortedDictionary<string, int>(_byEndpoint, StringComparer.Ordinal),
                byStatus = new SortedDictionary<string, int>(_byStatus, StringComparer.Ordinal),
                since = _since,
            };
        }
    }
}

/// <summary>Marks an endpoint as a "real" service endpoint: counted in stats and subject to scenario modes.</summary>
public sealed class ScenarioScoped
{
    public static readonly ScenarioScoped Instance = new();
}

public static class ScenarioEndpointExtensions
{
    public static TBuilder AsServiceEndpoint<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(ScenarioScoped.Instance);
}

/// <summary>
/// Applies the common modes (latency, slow, timeout, offline, http500) to service endpoints and counts them.
/// Admin and health endpoints carry no <see cref="ScenarioScoped"/> marker and pass straight through.
/// Service-specific modes are handled by each service's handlers.
/// </summary>
public sealed class ScenarioMiddleware(RequestDelegate next, ScenarioState scenario, RequestStats stats)
{
    private static readonly TimeSpan SlowDelay = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan TimeoutDelay = TimeSpan.FromSeconds(30);

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<ScenarioScoped>() is null)
        {
            await next(context);
            return;
        }

        var route = endpoint is RouteEndpoint re ? RouteText(re.RoutePattern) : context.Request.Path.Value ?? "";
        var key = $"{context.Request.Method} {route}";
        var status = "500";
        try
        {
            status = await ApplyAsync(context);
        }
        finally
        {
            stats.Record(key, status);
        }
    }

    private async Task<string> ApplyAsync(HttpContext context)
    {
        var current = scenario.Snapshot();
        if (current.Mode == Modes.Offline)
        {
            context.Abort();
            return "aborted";
        }

        var aborted = context.RequestAborted;
        try
        {
            if (current.LatencyMs > 0) await Task.Delay(current.LatencyMs, aborted);
            switch (current.Mode)
            {
                case Modes.Slow:
                    await Task.Delay(SlowDelay, aborted);
                    break;
                case Modes.Timeout:
                    await Task.Delay(TimeoutDelay, aborted);
                    await Results.Json(new { error = "simulated_timeout" }, statusCode: StatusCodes.Status504GatewayTimeout).ExecuteAsync(context);
                    return "504";
                case Modes.Http500:
                    await Results.Json(new { error = "simulated_server_error" }, statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context);
                    return "500";
            }
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            return "client_aborted";
        }

        await next(context);
        return context.Response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string RouteText(RoutePattern pattern)
    {
        var raw = pattern.RawText ?? "";
        return raw.StartsWith('/') ? raw : "/" + raw;
    }
}
