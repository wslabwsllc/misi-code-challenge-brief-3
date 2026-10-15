using System.Net.Http.Json;
using System.Text.Json;
using EventPermits.Application;
using EventPermits.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EventPermits.Infrastructure.Integrations;

/// <summary>
/// Server-side access to the mocks' admin API (Scenario Panel, call counts). The admin key never reaches a browser;
/// only System Admins can trigger these calls (E3-03 AC4).
/// </summary>
public sealed class MockAdminClient(IHttpClientFactory httpFactory, IntegrationOptions options) : IMockAdmin
{
    public bool Supports(IntegrationKind kind) => BaseUrl(kind) is { Length: > 0 } && options.MockAdminKey.Length > 0;

    public Task<ExternalResult<MockScenario>> GetScenarioAsync(IntegrationKind kind, CancellationToken ct) =>
        SendAsync<MockScenario>(kind, HttpMethod.Get, "_admin/scenario", null, ParseScenario, ct);

    public Task<ExternalResult<MockScenario>> SetScenarioAsync(IntegrationKind kind, string mode, CancellationToken ct) =>
        SendAsync<MockScenario>(kind, HttpMethod.Put, "_admin/scenario", new { mode, latencyMs = 0 }, ParseScenario, ct);

    public Task<ExternalResult<MockStats>> GetStatsAsync(IntegrationKind kind, CancellationToken ct) =>
        SendAsync<MockStats>(kind, HttpMethod.Get, "_admin/stats", null, doc =>
        {
            var root = doc.RootElement;
            return new MockStats(
                root.TryGetProperty("service", out var s) ? s.GetString() ?? "" : "",
                root.TryGetProperty("total", out var t) ? t.GetInt32() : 0,
                Counts(root, "byEndpoint"),
                Counts(root, "byStatus"));
        }, ct);

    public Task<ExternalResult<bool>> ResetAsync(IntegrationKind kind, CancellationToken ct) =>
        SendAsync(kind, HttpMethod.Post, "_admin/reset", null, _ => true, ct);

    public Task<ExternalResult<string>> DropPartnerFileAsync(string kind, CancellationToken ct) =>
        SendAsync(IntegrationKind.PartnerFeed, HttpMethod.Post, "_admin/partner-feed/drop", new { kind }, doc =>
            doc.RootElement.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "", ct);

    private async Task<ExternalResult<T>> SendAsync<T>(IntegrationKind kind, HttpMethod method, string path, object? body, Func<JsonDocument, T> parse, CancellationToken ct)
    {
        if (!Supports(kind)) return new(ExternalCallStatus.Rejected, default, "This integration has no scenario controls configured.", null);
        var http = httpFactory.CreateClient("mock-admin");
        using var request = new HttpRequestMessage(method, new Uri(new Uri(BaseUrl(kind)!.TrimEnd('/') + "/"), path));
        request.Headers.Add("X-Mock-Admin-Key", options.MockAdminKey);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json.Options);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new(Resilience.StatusOf(response.StatusCode), default, $"The mock answered HTTP {(int)response.StatusCode}.", null);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return new(ExternalCallStatus.Ok, parse(doc), "ok", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(ExternalCallStatus.Unavailable, default, "The mock service is not reachable.", null);
        }
    }

    private string? BaseUrl(IntegrationKind kind) => kind switch
    {
        IntegrationKind.Registry => options.Registry.BaseUrl,
        IntegrationKind.Calendar => options.Calendar.BaseUrl,
        IntegrationKind.NotificationGateway => options.Notify.BaseUrl,
        IntegrationKind.Weather => options.Weather.BaseUrl,
        IntegrationKind.Geocoding => options.Geocode.BaseUrl,
        IntegrationKind.PartnerFeed => options.PartnerFeed.AdminUrl,
        _ => null,
    };

    private static MockScenario ParseScenario(JsonDocument doc)
    {
        var root = doc.RootElement;
        return new MockScenario(
            root.TryGetProperty("service", out var s) ? s.GetString() ?? "" : "",
            root.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "",
            root.TryGetProperty("availableModes", out var a) ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : []);
    }

    private static Dictionary<string, int> Counts(JsonElement root, string name) =>
        root.TryGetProperty(name, out var obj) && obj.ValueKind == JsonValueKind.Object
            ? obj.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32() : 0)
            : [];
}

/// <summary>Configuration as shown on the Integration Hub: URLs visible, secrets masked (brief §7.2).</summary>
public sealed class IntegrationSettings(IntegrationOptions options, StorageOptions storage) : IIntegrationSettings
{
    public IReadOnlyList<IntegrationConfigView> Describe() =>
    [
        new(IntegrationKind.Registry, "Applicant Registry", options.Registry.BaseUrl, new Dictionary<string, string> { ["X-API-Key"] = Mask(options.Registry.ApiKey), ["Timeout"] = $"{options.Registry.TimeoutSeconds} s" }),
        new(IntegrationKind.Calendar, "Regional Event Calendar", options.Calendar.BaseUrl, new Dictionary<string, string> { ["Client ID"] = options.Calendar.ClientId, ["Client secret"] = Mask(options.Calendar.ClientSecret), ["Grant"] = "client_credentials" }),
        new(IntegrationKind.NotificationGateway, "Notification Gateway", options.Notify.BaseUrl, new Dictionary<string, string> { ["X-API-Key"] = Mask(options.Notify.ApiKey), ["Webhook secret"] = Mask(options.Notify.WebhookSecret), ["Webhook path"] = "/webhooks/delivery" }),
        new(IntegrationKind.ObjectStorage, "Object Storage", string.IsNullOrEmpty(storage.ServiceUrl) ? $"AWS S3 ({storage.Region})" : storage.ServiceUrl, new Dictionary<string, string> { ["Bucket"] = storage.Bucket, ["Access key"] = string.IsNullOrEmpty(storage.AccessKey) ? "task role (default chain)" : Mask(storage.AccessKey), ["Public endpoint"] = string.IsNullOrEmpty(storage.PublicEndpoint) ? "regional" : storage.PublicEndpoint }),
        new(IntegrationKind.Weather, "Weather Advisories", options.Weather.BaseUrl, new Dictionary<string, string> { ["Authentication"] = "none (public read-only)", ["Cache"] = "15 min (rule setting)" }),
        new(IntegrationKind.PartnerFeed, "Partner Roster Feed", $"{storage.Bucket}/{options.PartnerFeed.DropPrefix}", new Dictionary<string, string> { ["Transport"] = "drop folder in object storage (SFTP simulation, OD-5)", ["Scenario admin"] = options.PartnerFeed.AdminUrl }),
        new(IntegrationKind.Geocoding, "Location Service", options.Geocode.BaseUrl, new Dictionary<string, string> { ["Authentication"] = "none" }),
    ];

    /// <summary>First two and last two characters only; short values fully masked. Never the whole secret.</summary>
    public static string Mask(string? secret) => string.IsNullOrEmpty(secret)
        ? "(not configured)"
        : secret.Length < 10 ? "••••••" : $"{secret[..2]}••••{secret[^2..]}";
}

public static partial class IntegrationRegistration
{
    static partial void AddClients(IServiceCollection services, IConfiguration config)
    {
        var options = IntegrationOptions.From(config);
        services.AddSingleton(options);
        services.AddTransient<CorrelationHandler>();
        services.AddTransient<AttemptRecorderHandler>();

        services.AddHttpClient<IApplicantRegistry, RegistryClient>(h => h.BaseAddress = Base(options.Registry.BaseUrl))
            .AddIntegrationPipeline(IntegrationKind.Registry, TimeSpan.FromSeconds(options.Registry.TimeoutSeconds));
        services.AddHttpClient("calendar", h => h.BaseAddress = Base(options.Calendar.BaseUrl))
            .AddIntegrationPipeline(IntegrationKind.Calendar, TimeSpan.FromSeconds(options.Calendar.TimeoutSeconds));
        services.AddSingleton<CalendarTokenProvider>();
        services.AddSingleton<IRegionalCalendar, CalendarClient>();
        services.AddHttpClient<INotificationGateway, NotificationGatewayClient>(h => h.BaseAddress = Base(options.Notify.BaseUrl))
            .AddIntegrationPipeline(IntegrationKind.NotificationGateway, TimeSpan.FromSeconds(options.Notify.TimeoutSeconds));
        services.AddHttpClient<IWeatherService, WeatherClient>(h => h.BaseAddress = Base(options.Weather.BaseUrl))
            .AddIntegrationPipeline(IntegrationKind.Weather, TimeSpan.FromSeconds(options.Weather.TimeoutSeconds), retries: 1);
        services.AddHttpClient<IGeocoder, GeocodeClient>(h => h.BaseAddress = Base(options.Geocode.BaseUrl))
            .AddIntegrationPipeline(IntegrationKind.Geocoding, TimeSpan.FromSeconds(options.Geocode.TimeoutSeconds), retries: 1);
        services.AddHttpClient("mock-admin", h => h.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IMockAdmin, MockAdminClient>();
        services.AddSingleton<IIntegrationSettings, IntegrationSettings>();
        services.AddSingleton<EventPermits.Application.Services.IWebhookVerifier, WebhookVerifier>();
    }

    private static Uri Base(string url) => new((string.IsNullOrWhiteSpace(url) ? "http://unconfigured.invalid" : url.TrimEnd('/')) + "/");
}
