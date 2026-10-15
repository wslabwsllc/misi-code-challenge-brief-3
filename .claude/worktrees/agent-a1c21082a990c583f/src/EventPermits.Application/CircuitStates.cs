using EventPermits.Domain;

namespace EventPermits.Application;

/// <summary>
/// Live circuit-breaker state of each outbound integration pipeline (brief §7.4), for the Integration Hub.
/// Returns null while a pipeline has not been used since the process started.
/// </summary>
public interface ICircuitStates
{
    string? StateOf(IntegrationKind kind);
}
