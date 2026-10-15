using EventPermits.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EventPermits.Infrastructure;

/// <summary>
/// PostgreSQL wiring. The connection string is configuration (deployability contract): SSL options such as
/// <c>SSL Mode=VerifyFull;Root Certificate=/app/certs/rds-global-bundle.pem</c> pass straight through, and
/// <c>Database:Password</c> (environment variable or key-per-file secret) overrides any password in the string.
/// </summary>
public static class Persistence
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = ResolveConnectionString(config);
        services.AddDbContextFactory<AppDbContext>(o => o
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(Persistence).Assembly.GetName().Name))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        // Identity's stores need a scoped context.
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
        services.AddScoped<AppDb>();
        return services;
    }

    public static string ResolveConnectionString(IConfiguration config)
    {
        var raw = config["Database:ConnectionString"];
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Database:ConnectionString is not configured.");
        var builder = new NpgsqlConnectionStringBuilder(raw);
        var password = config["Database:Password"];
        if (!string.IsNullOrEmpty(password)) builder.Password = password;
        return builder.ConnectionString;
    }
}

/// <summary>Design-time factory for <c>dotnet ef migrations</c>; never connects.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=design;Username=design;Password=design",
                o => o.MigrationsAssembly(typeof(Persistence).Assembly.GetName().Name))
            .Options);
}
