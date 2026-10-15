using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EventPermits.Infrastructure.Integrations;

public static partial class IntegrationRegistration
{
    /// <summary>Typed clients for the external systems (brief §7), all behind the shared adapter pipeline.</summary>
    public static IServiceCollection AddIntegrations(this IServiceCollection services, IConfiguration config)
    {
        AddClients(services, config);
        return services;
    }

    static partial void AddClients(IServiceCollection services, IConfiguration config);
}
