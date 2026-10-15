using System.Security.Claims;
using System.Text.Json.Serialization;
using EventPermits.Application;
using EventPermits.Infrastructure;
using EventPermits.Infrastructure.Integrations;
using EventPermits.Infrastructure.Seeding;
using EventPermits.Web.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Formatting.Compact;

namespace EventPermits.Web.Hosting;

public static class ServiceSetup
{
    /// <summary>
    /// Registers everything for the web host, the background worker or an admin command. Configuration only:
    /// the same image runs locally and in AWS (deployability contract).
    /// </summary>
    public static void AddEventPermits(this WebApplicationBuilder builder, HostRole role)
    {
        var config = builder.Configuration;
        AddSecretFiles(config);
        var services = builder.Services;

        builder.Host.UseSerilog((ctx, cfg) =>
        {
            cfg.ReadFrom.Configuration(ctx.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Service", role.ToString().ToLowerInvariant());
            if (ctx.Configuration.GetValue("Logs:Console", true)) cfg.WriteTo.Console(new CompactJsonFormatter());
            if (ctx.Configuration["Logs:CaptureFile"] is { Length: > 0 } capture) cfg.WriteTo.File(new CompactJsonFormatter(), capture);
        }, preserveStaticLogger: true);

        var hosting = config.GetSection("Hosting").Get<HostingOptions>() ?? new HostingOptions();
        services.AddSingleton(hosting);
        var cookiePolicy = hosting.BehindTlsProxy ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        if (hosting.BehindTlsProxy)
        {
            var networks = hosting.ParseNetworks().ToList();
            services.Configure<ForwardedHeadersOptions>(o =>
            {
                o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                if (networks.Count == 0) return; // framework default: loopback only
                o.KnownProxies.Clear();
                o.KnownIPNetworks.Clear();
                foreach (var network in networks) o.KnownIPNetworks.Add(network);
            });
            services.AddHsts(o => o.MaxAge = TimeSpan.FromDays(hosting.HstsMaxAgeDays));
        }

        services.AddSingleton(TimeProvider.System);
        services.AddPersistence(config);

        var storage = StorageOptions.From(config);
        services.AddSingleton(storage);
        services.AddSingleton<IObjectStore, S3ObjectStore>();
        services.AddSingleton(new ContentSecurityPolicy(ContentSecurityPolicy.OriginOf(storage.PublicEndpoint, storage.ServiceUrl)));
        services.AddSingleton<ISigner, HmacSigner>();
        services.AddSingleton<IPdfRenderer, PdfRenderer>();

        services.AddDataProtection()
            .SetApplicationName("event-permits")
            .PersistKeysToDbContext<AppDbContext>();

        services.AddHttpContextAccessor();
        services.AddScoped<CorrelationContext>();
        services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<CorrelationContext>());
        services.AddScoped<ActorResolver>();
        services.AddScoped<RuleService>();
        services.AddScoped<Seeder>();
        services.AddApplicationServices();
        services.AddIntegrations(config);

        services.AddIdentityCore<AppUser>(o =>
            {
                o.Password.RequiredLength = 12;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.User.RequireUniqueEmail = false;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddClaimsPrincipalFactory<AppClaimsFactory>();

        // Role changes and deactivation reach existing cookies within a minute; commands re-check immediately (E1-03).
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(o =>
        {
            o.LoginPath = "/account/login";
            o.LogoutPath = "/account/logout";
            o.AccessDeniedPath = "/access-denied";
            o.Cookie.Name = "eventpermits.auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = cookiePolicy;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = ctx => ApiAwareRedirect(ctx, StatusCodes.Status401Unauthorized);
            o.Events.OnRedirectToAccessDenied = ctx => ApiAwareRedirect(ctx, StatusCodes.Status403Forbidden);
        });
        services.AddAuthorization(o =>
        {
            foreach (var (capability, roles) in Cap.Matrix)
                o.AddPolicy(capability, p => p.RequireAuthenticatedUser().RequireRole(roles));
        });
        services.AddCascadingAuthenticationState();

        services.AddAntiforgery(o =>
        {
            o.HeaderName = ApiEndpoints.AntiforgeryHeader;
            o.Cookie.Name = "eventpermits.csrf";
            o.Cookie.SecurePolicy = cookiePolicy;
        });

        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
            // Reject unknown JSON properties so protected fields cannot be overposted.
            o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        });
        services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = Domain.Limits.MaxAttachmentBytes + 64 * 1024);
        services.AddProblemDetails();
        services.AddExceptionHandler<ProblemExceptionHandler>();
        services.AddOpenApi();

        services.AddHealthChecks()
            .AddCheck("app", () => HealthCheckResult.Healthy("Process is running."), tags: ["live"])
            .AddDbContextCheck<AppDbContext>("database", tags: ["ready"])
            .AddCheck<StorageHealthCheck>("objectStorage", failureStatus: HealthStatus.Degraded, tags: ["dependency"])
            .AddIntegrationHealthChecks();

        if (role == HostRole.Worker) services.AddWorkerJobs();

        services.AddRazorComponents()
            .AddInteractiveServerComponents(o =>
            {
                o.DetailedErrors = builder.Environment.IsDevelopment();
                o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
            })
            .AddHubOptions(o => o.MaximumReceiveMessageSize = 64 * 1024);
    }

    /// <summary>
    /// Local secrets arrive as one file per setting (written by init-local); in AWS the same names are environment
    /// variables injected from Secrets Manager. Secrets:Directory points at the mounted folder.
    /// </summary>
    public static void AddSecretFiles(ConfigurationManager config)
    {
        var dir = config["Secrets:Directory"];
        if (!string.IsNullOrWhiteSpace(dir)) config.AddKeyPerFile(dir, optional: true, reloadOnChange: false);
    }

    private static Task ApiAwareRedirect(Microsoft.AspNetCore.Authentication.RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> ctx, int status)
    {
        if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/files"))
        {
            ctx.Response.StatusCode = status;
            return Task.CompletedTask;
        }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    }
}

/// <summary>Adds every role and the display name from the user rows (the source of truth).</summary>
public sealed class AppClaimsFactory(UserManager<AppUser> users, RoleManager<IdentityRole> roles, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim("display_name", user.DisplayName));
        return identity;
    }
}

public sealed class StorageHealthCheck(IObjectStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        return await store.PingAsync(cts.Token)
            ? HealthCheckResult.Healthy("Bucket reachable.")
            : HealthCheckResult.Degraded("Bucket not reachable.");
    }
}
