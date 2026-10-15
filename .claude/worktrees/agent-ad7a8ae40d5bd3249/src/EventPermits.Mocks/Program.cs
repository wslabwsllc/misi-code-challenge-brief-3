namespace EventPermits.Mocks;

/// <summary>When the process started; fixes the calendar materialisation and the partner "clean" content.</summary>
public sealed record MockStartup(DateTime StartedAtUtc);

/// <summary>
/// One image, six fictional external services; each process runs exactly one, chosen by
/// `--service &lt;name&gt;` or Mock:Service. Not a top-level program so other projects can reference it without a Program clash.
/// </summary>
public static class MockProgram
{
    public static readonly IReadOnlyDictionary<string, Func<IMockService>> Services = new Dictionary<string, Func<IMockService>>(StringComparer.OrdinalIgnoreCase)
    {
        ["registry"] = () => new RegistryService(),
        ["calendar"] = () => new CalendarService(),
        ["notify"] = () => new NotifyService(),
        ["weather"] = () => new WeatherService(),
        ["geocode"] = () => new GeocodeService(),
        ["partner-feed"] = () => new PartnerFeedService(),
    };

    public static async Task<int> Main(string[] args)
    {
        // `probe <url>` runs before any host is built (container health checks; runtime images have no curl).
        if (args.Length > 0 && args[0] == "probe") return await Probe.RunAsync(args);
        var (app, exitCode) = CreateApp(args);
        if (app is null) return exitCode;
        await app.RunAsync();
        return 0;
    }

    /// <summary>Builds one mock service host (used by Main and by the integration tests, which start it on a loopback port).</summary>
    public static (WebApplication? App, int ExitCode) CreateApp(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Local Compose passes secrets as files named like Mock__AdminKey; in AWS the same keys arrive as env vars.
        var secretsDirectory = builder.Configuration["Mock:SecretsDirectory"];
        if (!string.IsNullOrWhiteSpace(secretsDirectory)) builder.Configuration.AddKeyPerFile(secretsDirectory, optional: true);
        // Added last so the command line wins: `--service registry` → Mock:Service.
        builder.Configuration.AddCommandLine(args, new Dictionary<string, string>
        {
            ["--service"] = "Mock:Service",
            ["--mode"] = "Mock:Mode",
        });

        var options = builder.Configuration.GetSection("Mock").Get<MockOptions>() ?? new MockOptions();
        if (!Services.TryGetValue(options.Service.Trim(), out var factory))
        {
            Console.Error.WriteLine($"Unknown or missing service '{options.Service}'. Use --service <{string.Join("|", Services.Keys)}> or Mock:Service.");
            return (null, 2);
        }
        var service = factory();

        ScenarioState scenario;
        try
        {
            scenario = new ScenarioState(service.Name, options.Mode, options.LatencyMs, service.Modes);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return (null, 2);
        }

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = false;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

        var dataDirectory = options.ResolveDataDirectory();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(scenario);
        builder.Services.AddSingleton(new MockStartup(Clock.UtcNow()));
        builder.Services.AddSingleton(ReferenceData.Load(dataDirectory));
        builder.Services.AddSingleton<RequestStats>();
        builder.Services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = $"EventPermits fictional {service.Name} service (mock)";
            document.Info.Version = "v1";
            return Task.CompletedTask;
        }));
        service.ConfigureServices(builder.Services, options);

        var app = builder.Build();
        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseRouting();
        app.UseMiddleware<ScenarioMiddleware>();

        app.MapOpenApi();
        app.MapCommonEndpoints();
        service.MapEndpoints(app);

        // Never log secret values; only whether they are present.
        app.Logger.LogInformation(
            "Mock service {Service} starting in mode {Mode} latency {LatencyMs} ms; data {DataDirectory}; admin key configured: {AdminKeyConfigured}",
            service.Name, scenario.Mode, scenario.Snapshot().LatencyMs, dataDirectory, !string.IsNullOrEmpty(options.AdminKey));
        return (app, 0);
    }
}
