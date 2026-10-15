using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace EventPermits.IntegrationTests.Support;

/// <summary>
/// The in-process app (see <see cref="AppFixture"/>) plus one headless Chromium. Tests sign in through the real
/// login form, check accessibility with axe (WCAG 2.1 A/AA tags) and, when asked, save screenshots for docs.
/// </summary>
public class BrowserFixture(TestInfrastructure infra) : AppFixture(infra)
{
    private IPlaywright? _playwright;
    public IBrowser Browser { get; private set; } = default!;

    /// <summary>Set E2E__Screenshots=1 to write desktop and phone screenshots into docs/screenshots.</summary>
    public static bool Screenshots => Environment.GetEnvironmentVariable("E2E__Screenshots") == "1";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        // Installs the Chromium build that matches the Playwright package (no-op when already present).
        var exit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exit != 0) throw new InvalidOperationException($"Playwright could not install Chromium (exit {exit}).");
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    /// <summary>A new browser context signed in as <paramref name="userName"/> (or anonymous when null).</summary>
    public async Task<IPage> PageAsync(string? userName = null, int width = 1280, int height = 900)
    {
        var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseAddress.ToString().TrimEnd('/'),
            ViewportSize = new ViewportSize { Width = width, Height = height },
            Locale = "en-US",
            TimezoneId = "UTC",
        });
        context.SetDefaultTimeout(15_000);
        var page = await context.NewPageAsync();
        if (userName is not null)
        {
            await page.GotoAsync("/account/login");
            await page.GetByTestId("login-username").FillAsync(userName);
            await page.GetByTestId("login-password").FillAsync(Password);
            await page.GetByTestId("login-submit").ClickAsync();
            await page.WaitForURLAsync("**/dashboard");
        }
        return page;
    }

    /// <summary>Opens a page and waits until the interactive circuit has rendered the page heading.</summary>
    public static async Task GoAsync(IPage page, string url)
    {
        await page.GotoAsync(url);
        await page.Locator("h1").First.WaitForAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>Fails with the rule ids and targets when axe finds any WCAG 2.1 A/AA violation.</summary>
    public static async Task AssertAccessibleAsync(IPage page, string context)
    {
        var results = await page.RunAxe(new AxeRunOptions
        {
            RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"] },
        });
        var problems = results.Violations.Select(v => $"{v.Id} ({v.Impact}): {v.Help} → {string.Join(" | ", v.Nodes.Take(3).Select(n => string.Join(",", n.Target)))}").ToList();
        Assert.True(problems.Count == 0, $"Accessibility violations on {context}:\n" + string.Join("\n", problems));
    }

    /// <summary>Desktop and phone screenshots (E16-05) when E2E__Screenshots=1.</summary>
    public async Task ScreenshotAsync(IPage page, string name)
    {
        if (!Screenshots) return;
        var dir = Path.Combine(RepoRoot(), "docs", "screenshots");
        Directory.CreateDirectory(dir);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(dir, $"{name}-desktop.png"), FullPage = true });
        await page.SetViewportSizeAsync(390, 844);
        await page.WaitForTimeoutAsync(300);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(dir, $"{name}-phone.png"), FullPage = true });
        await page.SetViewportSizeAsync(1280, 900);
    }

    /// <summary>No element wider than the viewport at 390 px (no horizontal page scroll on phones, E13-04).</summary>
    public static async Task AssertNoHorizontalScrollAsync(IPage page)
    {
        await page.SetViewportSizeAsync(390, 844);
        await page.WaitForTimeoutAsync(200);
        var overflow = await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1");
        await page.SetViewportSizeAsync(1280, 900);
        Assert.False(overflow, "The page scrolls horizontally at 390 px.");
    }

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EventPermits.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public override async ValueTask DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        _playwright?.Dispose();
        await base.DisposeAsync();
    }
}
