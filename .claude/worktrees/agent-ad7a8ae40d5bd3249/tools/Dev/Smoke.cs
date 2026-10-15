using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dev;

/// <summary>
/// End-to-end HTTP smoke against any running instance (story E0-08 AC4): health, landing page, anonymous access,
/// sign-in through the real login form for every demo account, /api/me roles, page reachability for the 26 routes,
/// one forbidden route, and (local only, with --yes) web restart persistence. Never prints password values.
/// </summary>
public static partial class Smoke
{
    /// <summary>The 26 counted pages and a user who may open each (plan section 3.2).</summary>
    public static readonly (string Route, string User)[] Pages =
    [
        ("/dashboard", "manager.echo"),
        ("/applications", "manager.echo"),
        ("/applications/new", "intake.alpha"),
        ("/applications/detail", "manager.echo"),
        ("/reviews", "reviewer.charlie"),
        ("/information-requests", "intake.alpha"),
        ("/documents", "manager.echo"),
        ("/calendar", "scheduler.delta"),
        ("/venues/availability", "scheduler.delta"),
        ("/resources", "scheduler.delta"),
        ("/approvals", "manager.echo"),
        ("/permits", "manager.echo"),
        ("/operations/roster", "attendant.foxtrot"),
        ("/operations/check-in", "attendant.foxtrot"),
        ("/incidents", "manager.echo"),
        ("/notifications", "manager.echo"),
        ("/integrations/messaging", "admin.india"),
        ("/reports", "auditor.hotel"),
        ("/integrations", "admin.india"),
        ("/integrations/registry", "reviewer.charlie"),
        ("/integrations/calendar", "scheduler.delta"),
        ("/integrations/weather", "manager.echo"),
        ("/integrations/imports", "admin.india"),
        ("/admin/venues", "admin.india"),
        ("/venues/map", "admin.india"),
        ("/admin/users-rules", "admin.india"),
    ];

    private static int _passed, _failed, _skipped;

    public static async Task<int> RunAsync(Options o)
    {
        _passed = _failed = _skipped = 0;
        var remote = o.Flag("--remote");
        var baseUrl = (o.Value("--url") ?? Commands.LocalBaseUrl).TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || (baseUri.Scheme != "http" && baseUri.Scheme != "https"))
            throw new DevException($"--url must be an absolute http(s) URL, got '{baseUrl}'.");
        var credentialsFile = o.Value("--credentials-file");
        var passwords = LoadPasswords(credentialsFile);

        Out.Step($"Smoke test against {baseUrl}{(remote ? " (remote mode: Docker-dependent steps are SKIPPED)" : "")}");
        Out.Info(credentialsFile is null ? "Passwords: documented local demo password for every account." : $"Passwords: read from {credentialsFile} (values are never printed).");

        using var anon = NewClient(baseUri);

        Out.Step("Health");
        await CheckAsync("GET /health/live is 200", async () => (await anon.GetAsync("/health/live")).StatusCode == HttpStatusCode.OK);
        await CheckAsync("GET /health/ready is 200", async () => (await anon.GetAsync("/health/ready")).StatusCode == HttpStatusCode.OK);
        await CheckAsync("GET /health status is not Unhealthy", async () =>
        {
            using var r = await anon.GetAsync("/health");
            var status = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString();
            return (status is "Healthy" or "Degraded", $"status {status}, HTTP {(int)r.StatusCode}");
        });

        Out.Step("Landing page and anonymous access");
        await CheckAsync("GET / is 200, mentions \"Memorial events\" and shows no password", async () =>
        {
            using var r = await anon.GetAsync("/");
            var html = await r.Content.ReadAsStringAsync();
            var leaked = passwords.Values.Append(Repo.DemoPassword).Distinct().Any(p => p.Length > 0 && html.Contains(p, StringComparison.Ordinal));
            var mentions = html.Contains("Memorial events", StringComparison.Ordinal);
            return (r.StatusCode == HttpStatusCode.OK && mentions && !leaked,
                $"HTTP {(int)r.StatusCode}; mentions 'Memorial events': {mentions}; password present: {leaked}");
        });
        await CheckAsync("anonymous GET /api/me is 401", async () =>
        {
            using var r = await anon.GetAsync("/api/me");
            return (r.StatusCode == HttpStatusCode.Unauthorized, $"HTTP {(int)r.StatusCode}");
        });
        await CheckAsync("anonymous GET /dashboard is 302 to /account/login", async () =>
        {
            using var r = await anon.GetAsync("/dashboard");
            var location = r.Headers.Location?.ToString() ?? "";
            return (r.StatusCode == HttpStatusCode.Redirect && location.Contains("/account/login", StringComparison.OrdinalIgnoreCase),
                $"HTTP {(int)r.StatusCode} -> {location}");
        });

        Out.Step("Sign in through the login form and check /api/me roles");
        var sessions = new Dictionary<string, HttpClient>();
        foreach (var (user, roles) in Commands.DemoAccounts)
        {
            if (!passwords.TryGetValue(user, out var password))
            {
                Record("FAIL", $"{user}: sign in", "no password for this user in the credentials file");
                continue;
            }
            var client = NewClient(baseUri);
            var signedIn = await CheckAsync($"{user}: sign in -> 302 /dashboard", async () =>
            {
                var (ok, detail) = await SignInAsync(client, user, password);
                return (ok, detail);
            });
            if (!signedIn) { client.Dispose(); continue; }
            sessions[user] = client;
            await CheckAsync($"{user}: /api/me roles = {string.Join("+", roles)}", async () =>
            {
                using var r = await client.GetAsync("/api/me");
                if (r.StatusCode != HttpStatusCode.OK) return (false, $"HTTP {(int)r.StatusCode}");
                var me = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
                var actual = me.TryGetProperty("roles", out var rs) && rs.ValueKind == JsonValueKind.Array
                    ? rs.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet() : [];
                var display = me.TryGetProperty("displayName", out var d) ? d.GetString() : null;
                var ok = actual.SetEquals(roles);
                if (me.TryGetProperty("userName", out var un) && un.ValueKind == JsonValueKind.String)
                    ok &= string.Equals(un.GetString(), user, StringComparison.OrdinalIgnoreCase);
                return (ok, $"{display}: {string.Join("+", actual.Order())}");
            });
        }

        Out.Step($"Page reachability ({Pages.Length} routes, no redirects followed)");
        foreach (var (route, user) in Pages)
        {
            if (!sessions.TryGetValue(user, out var client))
            {
                Record("FAIL", $"{route} as {user}", "user is not signed in");
                continue;
            }
            await CheckAsync($"{route} as {user} is 200", async () =>
            {
                using var r = await client.GetAsync(route);
                var location = r.Headers.Location is { } l ? $" -> {l}" : "";
                return (r.StatusCode == HttpStatusCode.OK, $"HTTP {(int)r.StatusCode}{location}");
            });
        }

        Out.Step("Forbidden route");
        if (sessions.TryGetValue("attendant.foxtrot", out var attendant))
        {
            await CheckAsync("/admin/users-rules as attendant.foxtrot is denied", async () =>
            {
                using var r = await attendant.GetAsync("/admin/users-rules");
                var location = r.Headers.Location?.ToString() ?? "";
                var body = await r.Content.ReadAsStringAsync();
                var denied = r.StatusCode == HttpStatusCode.Forbidden
                    || (r.StatusCode == HttpStatusCode.Redirect && AccessDenied().IsMatch(location))
                    || body.Contains("data-testid=\"forbidden\"", StringComparison.Ordinal);
                return (denied, $"HTTP {(int)r.StatusCode}{(location.Length > 0 ? " -> " + location : "")}");
            });
        }
        else Record("FAIL", "/admin/users-rules as attendant.foxtrot is denied", "attendant.foxtrot is not signed in");

        Out.Step("Restart persistence (docker compose restart web)");
        const string restartStep = "web restart: ready again and manager.echo signs in";
        if (remote) Record("SKIPPED (remote)", restartStep, "restarts local containers");
        else if (!o.Flag("--yes")) Record("SKIPPED (needs --yes)", restartStep, "restarts the web container of your local stack");
        else
        {
            var restart = await Proc.Compose(["restart", "web"]);
            if (!restart.Ok) Record("FAIL", restartStep, "docker compose restart web failed");
            else if (!await Proc.WaitHttpAsync($"{baseUrl}/health/ready", TimeSpan.FromSeconds(120))) Record("FAIL", restartStep, "/health/ready not 200 within 120 s");
            else if (!passwords.TryGetValue("manager.echo", out var pw)) Record("FAIL", restartStep, "no password for manager.echo");
            else
            {
                using var again = NewClient(baseUri);
                await CheckAsync(restartStep, async () =>
                {
                    var (ok, detail) = await SignInAsync(again, "manager.echo", pw);
                    if (!ok) return (false, detail);
                    using var me = await again.GetAsync("/api/me");
                    return (me.StatusCode == HttpStatusCode.OK, $"/api/me HTTP {(int)me.StatusCode}");
                });
            }
        }

        foreach (var c in sessions.Values) c.Dispose();
        Out.Step($"Smoke: {_passed} passed, {_failed} failed, {_skipped} skipped");
        return _failed == 0 ? 0 : 1;
    }

    private static Dictionary<string, string> LoadPasswords(string? file)
    {
        if (file is null) return Commands.DemoAccounts.ToDictionary(a => a.User, _ => Repo.DemoPassword);
        var path = Path.GetFullPath(file);
        if (!File.Exists(path)) throw new DevException($"Credentials file '{file}' does not exist.");
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                   ?? throw new DevException("Credentials file is empty.");
        }
        catch (JsonException)
        {
            // Do not echo the exception: it can quote file content.
            throw new DevException($"Credentials file '{file}' is not a JSON object of user name to password.");
        }
    }

    private static HttpClient NewClient(Uri baseUri) =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false, UseCookies = true })
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(30),
        };

    private static async Task<(bool Ok, string Detail)> SignInAsync(HttpClient client, string user, string password)
    {
        using var page = await client.GetAsync("/account/login");
        if (page.StatusCode != HttpStatusCode.OK) return (false, $"GET /account/login HTTP {(int)page.StatusCode}");
        var html = await page.Content.ReadAsStringAsync();
        var token = HiddenInput(html, "__RequestVerificationToken");
        if (token is null) return (false, "antiforgery token not found on the login page");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_handler"] = HiddenInput(html, "_handler") ?? "login",
            ["Input.UserName"] = user,
            ["Input.Password"] = password,
        });
        using var post = await client.PostAsync("/account/login", form);
        var location = post.Headers.Location?.ToString() ?? "";
        var ok = post.StatusCode == HttpStatusCode.Redirect && location.TrimEnd('/').EndsWith("/dashboard", StringComparison.OrdinalIgnoreCase);
        return (ok, $"HTTP {(int)post.StatusCode}{(location.Length > 0 ? " -> " + location : "")}");
    }

    private static string? HiddenInput(string html, string name)
    {
        foreach (Match input in InputTag().Matches(html))
        {
            var tag = input.Value;
            if (!tag.Contains($"name=\"{name}\"", StringComparison.Ordinal)) continue;
            var value = ValueAttr().Match(tag);
            return value.Success ? WebUtility.HtmlDecode(value.Groups[1].Value) : null;
        }
        return null;
    }

    private static async Task<bool> CheckAsync(string name, Func<Task<bool>> check) =>
        await CheckAsync(name, async () => (await check(), ""));

    private static async Task<bool> CheckAsync(string name, Func<Task<(bool Ok, string Detail)>> check)
    {
        try
        {
            var (ok, detail) = await check();
            Record(ok ? "PASS" : "FAIL", name, detail);
            return ok;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Record("FAIL", name, ex is TaskCanceledException ? "timed out" : ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private static void Record(string status, string name, string detail)
    {
        switch (status)
        {
            case "PASS": _passed++; break;
            case "FAIL": _failed++; break;
            default: _skipped++; break;
        }
        Console.WriteLine($"  {status,-7} {name}{(detail.Length > 0 ? $"  ({detail})" : "")}");
    }

    [GeneratedRegex("<input\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InputTag();

    [GeneratedRegex("\\bvalue=\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ValueAttr();

    [GeneratedRegex("access-?denied|forbidden", RegexOptions.IgnoreCase)]
    private static partial Regex AccessDenied();
}
