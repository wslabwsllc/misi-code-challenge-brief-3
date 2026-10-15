using EventPermits.IntegrationTests.Support;
using Microsoft.Playwright;

namespace EventPermits.BrowserTests;

public sealed class ShellFixture(TestInfrastructure infra) : BrowserFixture(infra);

/// <summary>Landing page, sign-in and the application shell (E0-04, E0-09, E1-01, E1-04).</summary>
public sealed class ShellTests(ShellFixture app) : IClassFixture<ShellFixture>
{
    [Fact]
    public async Task Landing_page_explains_the_app_without_credentials_and_is_accessible()
    {
        var page = await app.PageAsync();
        await page.GotoAsync("/");
        await Assertions.Expect(page.Locator("h1")).ToContainTextAsync("Memorial events");
        var html = await page.ContentAsync();
        Assert.DoesNotContain(AppFixture.Password, html);
        Assert.Contains("Fictional practice exercise", html);
        await BrowserFixture.AssertAccessibleAsync(page, "landing");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "00-landing");
        await page.GetByTestId("landing-sign-in").ClickAsync();
        await page.WaitForURLAsync("**/account/login");
        await BrowserFixture.AssertAccessibleAsync(page, "login");
        await app.ScreenshotAsync(page, "00-login");
    }

    [Fact]
    public async Task Sign_in_lands_on_the_dashboard_and_navigation_matches_the_role()
    {
        var page = await app.PageAsync("attendant.foxtrot");
        await BrowserFixture.GoAsync(page, "/dashboard");
        Assert.Equal(0, await page.GetByTestId("nav-18").CountAsync()); // attendants never see administration
        Assert.Equal(1, await page.GetByTestId("nav-14").CountAsync());
        var admin = await app.PageAsync("admin.india");
        await BrowserFixture.GoAsync(admin, "/dashboard");
        Assert.Equal(1, await admin.GetByTestId("nav-18").CountAsync());
        Assert.Equal(0, await admin.GetByTestId("nav-10").CountAsync()); // admins cannot approve
    }

    [Fact]
    public async Task Wrong_password_shows_an_accessible_error()
    {
        var page = await app.PageAsync();
        await page.GotoAsync("/account/login");
        await page.GetByTestId("login-username").FillAsync("intake.bravo");
        await page.GetByTestId("login-password").FillAsync("not-the-password");
        await page.GetByTestId("login-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("login-error")).ToBeVisibleAsync();
        Assert.Equal("alert", await page.GetByTestId("login-error").GetAttributeAsync("role"));
    }
}
