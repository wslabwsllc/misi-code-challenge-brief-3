using EventPermits.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EventPermits.Application;

public static partial class Registration
{
    static partial void AddServices(IServiceCollection services)
    {
        services.AddScoped<FileService>();
        services.AddScoped<CaseService>();
        services.AddScoped<RegisterService>();
        services.AddScoped<ReviewService>();
        services.AddScoped<SchedulingService>();
        services.AddScoped<CalendarSyncService>();
        services.AddScoped<ApprovalService>();
        services.AddScoped<PermitService>();
        services.AddScoped<OperationsService>();
        services.AddScoped<IncidentService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<AdminService>();
        services.AddScoped<IntegrationHubService>();
        services.AddScoped<WeatherQueryService>();
        services.AddScoped<GeocodingService>();
        services.AddScoped<PartnerImportService>();
        services.AddScoped<DashboardService>();
    }
}
