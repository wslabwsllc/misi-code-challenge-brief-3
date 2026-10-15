using Microsoft.Extensions.DependencyInjection;

namespace EventPermits.Application;

public static partial class Registration
{
    /// <summary>Application services (one per business area). Each is scoped and creates its own short-lived contexts.</summary>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        AddServices(services);
        return services;
    }

    static partial void AddServices(IServiceCollection services);
}
