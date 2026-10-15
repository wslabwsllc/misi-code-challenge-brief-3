using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EventPermits.Web.Hosting;

public static partial class Jobs
{
    /// <summary>Scheduled jobs of the worker process (E3-06), each guarded by a PostgreSQL advisory lock.</summary>
    public static IServiceCollection AddWorkerJobs(this IServiceCollection services)
    {
        AddJobs(services);
        return services;
    }

    /// <summary>One degraded-not-dead check per external integration (FR-16).</summary>
    public static IHealthChecksBuilder AddIntegrationHealthChecks(this IHealthChecksBuilder builder)
    {
        AddChecks(builder);
        return builder;
    }

    static partial void AddJobs(IServiceCollection services);
    static partial void AddChecks(IHealthChecksBuilder builder);
}
