using EventPermits.Application;
using EventPermits.Infrastructure.Seeding;
using EventPermits.Mocks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EventPermits.IntegrationTests.Support;

/// <summary>Controllable clock for numbering, windows and expiry tests.</summary>
public sealed class TestClock : TimeProvider
{
    public DateTimeOffset? Fixed { get; set; }
    public TimeSpan Offset { get; set; }
    public override DateTimeOffset GetUtcNow() => (Fixed ?? DateTimeOffset.UtcNow) + Offset;
}

/// <summary>
/// One complete system per test class: the web app on a real Kestrel port, the six mock services on loopback ports,
/// its own PostgreSQL database and bucket, and the seed (users and reference data) applied.
/// </summary>
public class AppFixture(TestInfrastructure infra) : IAsyncLifetime
{
    public const string MockAdminKey = "mock-admin-key-for-tests-0123456789";
    public const string RegistryKey = "registry-key-for-tests-0123456789";
    public const string CalendarSecret = "calendar-secret-for-tests-0123456789";
    public const string NotifyKey = "notify-key-for-tests-0123456789";
    public const string WebhookSecret = "webhook-secret-for-tests-0123456789-abcdef";
    public static readonly string Password = DemoAccounts.DocumentedPassword;

    private readonly List<WebApplication> _mocks = [];
    public WebApplicationFactory<Program> Web { get; private set; } = default!;
    public Uri BaseAddress { get; private set; } = default!;
    public TestClock Clock { get; } = new();
    public string ConnectionString { get; private set; } = "";
    public string Bucket { get; private set; } = "";
    public string LogFile { get; } = Path.Combine(Path.GetTempPath(), "eventpermits-tests", Guid.NewGuid().ToString("N") + ".jsonl");
    public Dictionary<string, Uri> MockUrls { get; } = [];
    public Dictionary<string, string> UserIds { get; } = [];
    protected virtual void Configure(IWebHostBuilder builder) { }

    public virtual async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
        ConnectionString = await infra.NewDatabaseAsync();
        Bucket = await infra.NewBucketAsync();

        foreach (var service in new[] { "registry", "calendar", "weather", "geocode", "partner-feed" })
            MockUrls[service] = await StartMockAsync(service, TestInfrastructure.FreePort(), null);
        var notifyPort = TestInfrastructure.FreePort();
        MockUrls["notify"] = new Uri($"http://127.0.0.1:{notifyPort}");

        Web = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            // A dynamic loopback port, so parallel test runs never fight over Kestrel's default 5000.
            b.UseSetting("urls", "http://127.0.0.1:0");
            b.UseStaticWebAssets();
            foreach (var (key, value) in Settings()) b.UseSetting(key, value);
            b.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
            Configure(b);
        });
        Web.UseKestrel(0);
        Web.StartServer();
        BaseAddress = Web.ClientOptions.BaseAddress;
        // The gateway calls back the web app, so it starts once the web port is known.
        await StartMockAsync("notify", notifyPort, new Uri(BaseAddress, "/webhooks/delivery").ToString());

        await using var scope = Web.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<Seeder>().SeedAsync();
        await using (var db = await factory.CreateDbContextAsync())
            foreach (var u in await db.Users.ToListAsync()) UserIds[u.UserName!] = u.Id;
    }

    public IEnumerable<KeyValuePair<string, string>> Settings() => new Dictionary<string, string>
    {
        ["Database:ConnectionString"] = ConnectionString,
        ["Storage:ServiceUrl"] = infra.StorageUrl,
        ["Storage:PublicEndpoint"] = infra.StorageUrl,
        ["Storage:ForcePathStyle"] = "true",
        ["Storage:Bucket"] = Bucket,
        ["Storage:AccessKey"] = TestInfrastructure.AppKey,
        ["Storage:SecretKey"] = TestInfrastructure.AppSecret,
        ["Security:PermitTokenKey"] = "permit-token-key-for-tests-0123456789-abcdefghij",
        ["Security:LinkSigningKey"] = "link-signing-key-for-tests-0123456789-abcdefghij",
        ["Integrations:Registry:BaseUrl"] = MockUrls["registry"].ToString(),
        ["Integrations:Registry:ApiKey"] = RegistryKey,
        ["Integrations:Registry:TimeoutSeconds"] = "2",
        ["Integrations:Calendar:BaseUrl"] = MockUrls["calendar"].ToString(),
        ["Integrations:Calendar:ClientId"] = "event-permits-app",
        ["Integrations:Calendar:ClientSecret"] = CalendarSecret,
        ["Integrations:Calendar:TimeoutSeconds"] = "2",
        ["Integrations:Notify:BaseUrl"] = MockUrls["notify"].ToString(),
        ["Integrations:Notify:ApiKey"] = NotifyKey,
        ["Integrations:Notify:WebhookSecret"] = WebhookSecret,
        ["Integrations:Notify:TimeoutSeconds"] = "2",
        ["Integrations:Weather:BaseUrl"] = MockUrls["weather"].ToString(),
        ["Integrations:Weather:TimeoutSeconds"] = "2",
        ["Integrations:Geocode:BaseUrl"] = MockUrls["geocode"].ToString(),
        ["Integrations:Geocode:TimeoutSeconds"] = "2",
        ["Integrations:PartnerFeed:AdminUrl"] = MockUrls["partner-feed"].ToString(),
        ["Integrations:MockAdminKey"] = MockAdminKey,
        ["Seed:AllowDemoPassword"] = "true",
        ["Logs:Console"] = "false",
        ["Logs:CaptureFile"] = LogFile,
    };

    private async Task<Uri> StartMockAsync(string service, int port, string? webhookUrl)
    {
        var args = new List<string>
        {
            "--service", service, "--urls", $"http://127.0.0.1:{port}",
            "--Mock:AdminKey", MockAdminKey, "--Mock:Registry:ApiKey", RegistryKey,
            "--Mock:Calendar:ClientId", "event-permits-app", "--Mock:Calendar:ClientSecret", CalendarSecret,
            "--Mock:Notify:ApiKey", NotifyKey, "--Mock:Notify:WebhookSecret", WebhookSecret,
            "--Mock:PartnerFeed:ServiceUrl", infra.StorageUrl, "--Mock:PartnerFeed:ForcePathStyle", "true", "--Mock:PartnerFeed:Bucket", Bucket,
            "--Mock:PartnerFeed:AccessKey", TestInfrastructure.PartnerKey, "--Mock:PartnerFeed:SecretKey", TestInfrastructure.PartnerSecret,
            "--Logging:LogLevel:Default", "Warning",
        };
        if (webhookUrl is not null) args.AddRange(["--Mock:Notify:WebhookUrl", webhookUrl]);
        var (app, code) = MockProgram.CreateApp(args.ToArray());
        if (app is null) throw new InvalidOperationException($"Mock {service} did not start (exit {code}).");
        await app.StartAsync();
        _mocks.Add(app);
        return new Uri($"http://127.0.0.1:{port}");
    }

    public ApiClient Client() => new(BaseAddress);

    public async Task<ApiClient> SignInAsync(string userName)
    {
        var client = Client();
        await client.SignInAsync(userName, Password);
        return client;
    }

    public async Task<T> WithServiceAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Web.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public async Task WithDbAsync(Func<AppDbContext, Task> work)
    {
        await using var scope = Web.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        await work(db);
    }

    public async Task<T> QueryAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        await using var scope = Web.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        return await work(db);
    }

    public async Task<Actor> ActorAsync(string userName) =>
        await WithServiceAsync(sp => sp.GetRequiredService<ActorResolver>().ResolveAsync(UserIds[userName]));

    public MockAdminClient MockAdmin(string service) => new(MockUrls[service], MockAdminKey);

    public virtual async ValueTask DisposeAsync()
    {
        foreach (var mock in _mocks)
        {
            await mock.StopAsync();
            await mock.DisposeAsync();
        }
        await Web.DisposeAsync();
    }
}
