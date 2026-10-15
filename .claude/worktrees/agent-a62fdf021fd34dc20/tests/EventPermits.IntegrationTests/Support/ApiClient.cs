using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EventPermits.IntegrationTests.Support;

/// <summary>An HTTP response with its status and parsed body (ProblemDetails for errors).</summary>
public sealed record ApiResult(HttpStatusCode Status, JsonNode? Body, string Raw)
{
    public string? Code => Body?["code"]?.GetValue<string>();
    public string? Title => Body?["title"]?.GetValue<string>();
    public T As<T>() => Body.Deserialize<T>(ApiClient.Json)!;

    public ApiResult Expect(HttpStatusCode status)
    {
        Assert.True(Status == status, $"Expected {(int)status} but got {(int)Status}: {Raw}");
        return this;
    }

    public ApiResult Ok() => Status is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent or HttpStatusCode.Accepted
        ? this
        : throw new Xunit.Sdk.XunitException($"Expected success but got {(int)Status}: {Raw}");

    public uint Version => Body?["version"]?.GetValue<uint>() ?? 0;
    public Guid Id => Guid.Parse(Body!["id"]!.GetValue<string>());
}

/// <summary>A browser-like client: cookie session from the real sign-in form, anti-forgery header on writes.</summary>
public sealed partial class ApiClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private string? _csrf;

    public ApiClient(Uri baseAddress)
    {
        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies, AllowAutoRedirect = false }) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(60) };
    }

    public HttpClient Http => _http;

    public async Task SignInAsync(string userName, string password)
    {
        var page = await _http.GetStringAsync("/account/login");
        var input = TokenRegex().Match(page).Value;
        var token = ValueRegex().Match(input).Groups[1].Value;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["_handler"] = "login", ["Input.UserName"] = userName, ["Input.Password"] = password,
        });
        using var response = await _http.PostAsync("/account/login", form);
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"Sign-in of {userName} failed with {(int)response.StatusCode}.");
        var csrf = await GetAsync("/api/antiforgery");
        _csrf = csrf.Body!["token"]!.GetValue<string>();
    }

    public Task<ApiResult> GetAsync(string url) => SendAsync(HttpMethod.Get, url, null);
    public Task<ApiResult> PostAsync(string url, object? body = null) => SendAsync(HttpMethod.Post, url, body ?? new { });
    public Task<ApiResult> PutAsync(string url, object body) => SendAsync(HttpMethod.Put, url, body);

    public async Task<ApiResult> SendAsync(HttpMethod method, string url, object? body, bool antiforgery = true, IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = body is HttpContent content ? content : JsonContent.Create(body, options: Json);
        if (antiforgery && _csrf is not null && method != HttpMethod.Get) request.Headers.Add("X-CSRF-TOKEN", _csrf);
        if (headers is not null) foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
        using var response = await _http.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        JsonNode? node = null;
        if (raw.Length > 0 && (response.Content.Headers.ContentType?.MediaType?.Contains("json") ?? false))
            node = JsonNode.Parse(raw);
        return new ApiResult(response.StatusCode, node, raw);
    }

    public async Task<ApiResult> UploadAsync(string url, string fileName, string contentType, byte[] bytes, IDictionary<string, string> fields)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        foreach (var (k, v) in fields) form.Add(new StringContent(v), k);
        return await SendAsync(HttpMethod.Post, url, form);
    }

    public async Task<HttpResponseMessage> RawGetAsync(string url) => await _http.GetAsync(url);

    public void Dispose() => _http.Dispose();

    [GeneratedRegex("<input[^>]*__RequestVerificationToken[^>]*>")]
    private static partial Regex TokenRegex();

    [GeneratedRegex("value=\"([^\"]+)\"")]
    private static partial Regex ValueRegex();
}

/// <summary>Direct access to a mock's admin API (scenario modes and call counts).</summary>
public sealed class MockAdminClient(Uri baseUrl, string adminKey)
{
    private static readonly HttpClient Http = new();

    public async Task SetModeAsync(string mode)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(baseUrl, "/_admin/scenario")) { Content = JsonContent.Create(new { mode, latencyMs = 0 }) };
        request.Headers.Add("X-Mock-Admin-Key", adminKey);
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    public async Task<int> TotalCallsAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, "/_admin/stats"));
        request.Headers.Add("X-Mock-Admin-Key", adminKey);
        using var response = await Http.SendAsync(request);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return node!["total"]!.GetValue<int>();
    }

    public async Task<JsonNode> PostAsync(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, path)) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Mock-Admin-Key", adminKey);
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }
}
