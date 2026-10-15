using System.Collections.Concurrent;
using EventPermits.Application;
using EventPermits.Domain;
using Polly.CircuitBreaker;

namespace EventPermits.Infrastructure.Integrations;

/// <summary>One Polly state provider per integration pipeline, so the hub can show Closed / Open / HalfOpen.</summary>
public sealed class CircuitStateRegistry : ICircuitStates
{
    private readonly ConcurrentDictionary<IntegrationKind, CircuitBreakerStateProvider> _providers = new();

    /// <summary>A fresh provider for a (re)built pipeline; a provider can be attached to one breaker only.</summary>
    public CircuitBreakerStateProvider For(IntegrationKind kind)
    {
        var provider = new CircuitBreakerStateProvider();
        _providers[kind] = provider;
        return provider;
    }

    public string? StateOf(IntegrationKind kind)
    {
        if (!_providers.TryGetValue(kind, out var provider)) return null;
        try
        {
            return provider.CircuitState.ToString();
        }
        catch (InvalidOperationException)
        {
            return null; // pipeline registered but not built yet
        }
    }
}
