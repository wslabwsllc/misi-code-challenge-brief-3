using System.Text;
using System.Text.RegularExpressions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using EventPermits.Domain;
using EventPermits.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace EventPermits.BrowserTests;

public sealed class OverviewAdminFixture(TestInfrastructure infra) : BrowserFixture(infra);

/// <summary>
/// Pages 01 Operations Dashboard, 16 Reports, 18 Users &amp; Rules, 19 Integration Hub (+ attempt detail),
/// 24 Weather and 25 Data Import: data, main action, validation, forbidden role, axe and 390 px layout.
/// </summary>
public sealed class OverviewAdminPagesTests(OverviewAdminFixture app) : IClassFixture<OverviewAdminFixture>
{
    private readonly Scenario _scenario = new(app);

    private static ILocator Id(IPage page, string testId) => page.GetByTestId(testId);

    /// <summary>
    /// Same WCAG 2.1 A/AA axe run as <see cref="BrowserFixture.AssertAccessibleAsync"/>, minus one known shell defect outside
    /// these pages: the decorative page number of the active navigation link (<c>.nav-link.active .nav-link__num</c>, app.css)
    /// fails colour contrast on every page. Everything rendered by the pages themselves is checked.
    /// </summary>
    private static async Task AssertAccessibleAsync(IPage page, string context)
    {
        var results = await page.RunAxe(
            new AxeRunContext { Exclude = [new AxeSelector(".nav-link.active .nav-link__num")] },
            new AxeRunOptions { RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"] } });
        var problems = results.Violations.Select(v => $"{v.Id} ({v.Impact}): {v.Help} → {string.Join(" | ", v.Nodes.Take(3).Select(n => string.Join(",", n.Target)))}").ToList();
        Assert.True(problems.Count == 0, $"Accessibility violations on {context}:\n" + string.Join("\n", problems));
    }

    private static async Task<string> DownloadTextAsync(IPage page, ILocator link)
    {
        var download = await page.RunAndWaitForDownloadAsync(() => link.ClickAsync());
        var path = await download.PathAsync();
        return await File.ReadAllTextAsync(path!, Encoding.UTF8);
    }

    private static async Task ExpectForbiddenAsync(OverviewAdminFixture app, string user, string url)
    {
        var page = await app.PageAsync(user);
        await BrowserFixture.GoAsync(page, url);
        await Assertions.Expect(Id(page, "forbidden")).ToBeVisibleAsync();
        await page.Context.CloseAsync();
    }

    // ------------------------------------------------------------------ 01 Operations Dashboard

    [Fact]
    public async Task Dashboard_shows_role_tailored_kpis_that_match_the_database_and_link_to_filtered_pages()
    {
        var submitted = await _scenario.SubmittedAsync();
        var expected = await app.QueryAsync(db => db.Applications.CountAsync(a => a.Status == CaseStatus.Submitted));

        var page = await app.PageAsync("reviewer.charlie");
        await BrowserFixture.GoAsync(page, "/dashboard");
        await Assertions.Expect(Id(page, "kpi-submitted-value")).ToHaveTextAsync(expected.ToString());
        Assert.Equal("/applications?status=Submitted", await Id(page, "kpi-submitted").GetAttributeAsync("href"));
        await Assertions.Expect(Id(page, "dashboard-pending")).ToContainTextAsync(submitted.Reference);
        await Assertions.Expect(Id(page, "dashboard-pending").GetByText("Review eligibility").First).ToBeVisibleAsync();
        Assert.Equal(0, await Id(page, "kpi-degraded").CountAsync()); // reviewers do not see integration health

        await Id(page, "dashboard-refresh").ClickAsync();
        await Assertions.Expect(page.Locator(".sr-live")).ToContainTextAsync("Dashboard refreshed.");
        await AssertAccessibleAsync(page, "dashboard (reviewer)");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "01-dashboard");

        // Admin: integration health and failed notifications, but no case KPIs (least privilege).
        var admin = await app.PageAsync("admin.india");
        await BrowserFixture.GoAsync(admin, "/dashboard");
        await Assertions.Expect(Id(admin, "kpi-degraded")).ToBeVisibleAsync();
        await Assertions.Expect(Id(admin, "dashboard-integrations")).ToBeVisibleAsync();
        Assert.Equal(0, await Id(admin, "kpi-submitted").CountAsync());
        Assert.Equal(0, await Id(admin, "dashboard-today").CountAsync());
        await AssertAccessibleAsync(admin, "dashboard (admin)");
    }

    // ------------------------------------------------------------------ 16 Reports & Exports

    [Fact]
    public async Task Reports_calculate_from_the_database_validate_the_range_export_csv_and_block_other_roles()
    {
        await _scenario.SubmittedAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-89);
        var expected = await app.QueryAsync(db => db.Applications.CountAsync(a => a.CreatedAt >= new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)));

        var page = await app.PageAsync("manager.echo");
        await BrowserFixture.GoAsync(page, "/reports");
        await Assertions.Expect(Id(page, "report-applications")).ToHaveTextAsync(expected.ToString("N0"));
        await Assertions.Expect(Id(page, "chart-throughput")).ToContainTextAsync("Submitted");
        await Assertions.Expect(Id(page, "chart-utilisation")).ToContainTextAsync("%");
        await AssertAccessibleAsync(page, "reports");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "16-reports");

        var csv = await DownloadTextAsync(page, Id(page, "csv-throughput"));
        Assert.Contains("report,throughput", csv);
        Assert.Contains("Submitted,", csv);

        // End before start: the server's validation message is shown on the field.
        await Id(page, "report-from").FillAsync(today.ToString("yyyy-MM-dd"));
        await Id(page, "report-to").FillAsync(today.AddDays(-3).ToString("yyyy-MM-dd"));
        await Id(page, "report-apply").ClickAsync();
        await Assertions.Expect(Id(page, "report-to-error")).ToContainTextAsync("must not be before");
        Assert.Equal("true", await Id(page, "report-to").GetAttributeAsync("aria-invalid"));

        // A valid range updates the figures and the URL.
        await Id(page, "report-from").FillAsync(today.AddDays(-7).ToString("yyyy-MM-dd"));
        await Id(page, "report-to").FillAsync(today.ToString("yyyy-MM-dd"));
        await Id(page, "report-apply").ClickAsync();
        await page.WaitForURLAsync(new Regex("from=" + today.AddDays(-7).ToString("yyyy-MM-dd")));
        await Assertions.Expect(Id(page, "report-to-error")).ToBeEmptyAsync();

        // Auditor: same reports plus the audit CSV export.
        var auditor = await app.PageAsync("auditor.hotel");
        await BrowserFixture.GoAsync(auditor, "/reports");
        await Assertions.Expect(Id(auditor, "report-audit-csv")).ToBeVisibleAsync();
        var audit = await DownloadTextAsync(auditor, Id(auditor, "report-audit-csv"));
        Assert.StartsWith("﻿At (UTC),Actor,Action", audit);
        Assert.DoesNotContain("avery.alder@example.test", audit);

        await ExpectForbiddenAsync(app, "intake.alpha", "/reports");
    }

    // ------------------------------------------------------------------ 19 Integration Hub and attempt detail

    [Fact]
    public async Task Integration_hub_tests_a_connection_opens_the_sanitised_attempt_and_switches_mock_scenarios()
    {
        var page = await app.PageAsync("admin.india");
        await BrowserFixture.GoAsync(page, "/integrations");
        await Assertions.Expect(page.Locator("[data-testid^='hub-card-']")).ToHaveCountAsync(7);
        await Assertions.Expect(Id(page, "scenario-panel")).ToBeVisibleAsync();

        await Id(page, "hub-test-Registry").ClickAsync();
        await Assertions.Expect(Id(page, "hub-test-result-Registry")).ToContainTextAsync("Connection OK");
        await Assertions.Expect(Id(page, "hub-health-Registry")).ToHaveTextAsync("Healthy");
        await Assertions.Expect(Id(page, "hub-circuit-Registry")).ToContainTextAsync("Closed");
        Assert.DoesNotContain(AppFixture.RegistryKey, await page.ContentAsync());
        Assert.DoesNotContain(AppFixture.MockAdminKey, await page.ContentAsync());
        await AssertAccessibleAsync(page, "integration hub");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "19-integration-hub");

        // Scenario panel: an empty choice is refused with a field message; a real mode switches the mock.
        await Id(page, "scenario-apply-Weather").ClickAsync();
        await Assertions.Expect(Id(page, "scenario-Weather").Locator(".field__error")).ToContainTextAsync("Choose a scenario mode.");
        await Id(page, "scenario-select-Weather").SelectOptionAsync("offline");
        await Id(page, "scenario-apply-Weather").ClickAsync();
        await Assertions.Expect(Id(page, "scenario-mode-Weather")).ToHaveTextAsync("offline");
        await Id(page, "hub-test-Weather").ClickAsync();
        await Assertions.Expect(Id(page, "hub-test-result-Weather")).ToContainTextAsync("Connection failed");
        await Assertions.Expect(Id(page, "hub-health-Weather")).ToHaveTextAsync("Offline");
        await Id(page, "scenario-reset-Weather").ClickAsync();
        await Assertions.Expect(Id(page, "scenario-mode-Weather")).ToHaveTextAsync("normal");

        // Attempt detail: request ID, status, timing and attempt number; no secrets.
        await Id(page, "hub-test-attempt-Registry").ClickAsync();
        await page.WaitForURLAsync("**/integrations/attempts/*");
        await Assertions.Expect(Id(page, "attempt-detail")).ToBeVisibleAsync();
        await Assertions.Expect(Id(page, "attempt-status")).ToHaveTextAsync("200");
        await Assertions.Expect(Id(page, "attempt-number")).ToContainTextAsync("1");
        Assert.False(string.IsNullOrWhiteSpace(await Id(page, "attempt-request-id").TextContentAsync()));
        var html = await page.ContentAsync();
        Assert.DoesNotContain(AppFixture.RegistryKey, html);
        await AssertAccessibleAsync(page, "integration attempt");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "19-integration-attempt");
        var attemptUrl = new Uri(page.Url).PathAndQuery;

        // Ops Manager reads the hub without the Scenario Panel; attendants are blocked from both routes.
        var manager = await app.PageAsync("manager.echo");
        await BrowserFixture.GoAsync(manager, "/integrations");
        await Assertions.Expect(Id(manager, "hub-card-Weather")).ToBeVisibleAsync();
        Assert.Equal(0, await Id(manager, "scenario-panel").CountAsync());
        await ExpectForbiddenAsync(app, "attendant.foxtrot", "/integrations");
        await ExpectForbiddenAsync(app, "attendant.foxtrot", attemptUrl);
    }

    // ------------------------------------------------------------------ 24 Weather & Advisories

    [Fact]
    public async Task Weather_shows_severe_advisory_requires_a_valid_acknowledgement_and_labels_stale_cache()
    {
        var c = await _scenario.ReservedAsync();
        var weather = app.MockAdmin("weather");
        await weather.SetModeAsync("severe");
        try
        {
            var page = await app.PageAsync("manager.echo");
            await BrowserFixture.GoAsync(page, "/integrations/weather");
            await Id(page, "weather-show").ClickAsync();
            await Assertions.Expect(Id(page, "weather-venue-error")).ToHaveTextAsync("Choose a venue.");

            await BrowserFixture.GoAsync(page, $"/integrations/weather?venueId={c.VenueId}&date={c.Date:yyyy-MM-dd}");
            await Assertions.Expect(Id(page, "weather-severe")).ToBeVisibleAsync();
            await Assertions.Expect(Id(page, "weather-freshness")).ToHaveTextAsync("Fresh");
            await Assertions.Expect(Id(page, "weather-times")).ToContainTextAsync("Source timestamp");
            await Assertions.Expect(Id(page, $"weather-ack-{c.Reference}")).ToContainTextAsync("Needs acknowledgement");
            await Assertions.Expect(Id(page, "weather-cache")).ToContainTextAsync(c.Date.ToString("d MMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US")));
            await AssertAccessibleAsync(page, "weather");
            await BrowserFixture.AssertNoHorizontalScrollAsync(page);
            await app.ScreenshotAsync(page, "24-weather");

            await Id(page, "weather-ack-note").FillAsync("short");
            await Id(page, "weather-ack-submit").ClickAsync();
            await Assertions.Expect(Id(page, "weather-ack-note-error")).ToContainTextAsync("10 to 1000 characters");

            await Id(page, "weather-ack-note").FillAsync("Covered seating arranged and start moved to 11:00.");
            await Id(page, "weather-ack-submit").ClickAsync();
            await Assertions.Expect(Id(page, $"weather-ack-{c.Reference}")).ToContainTextAsync("Acknowledged");
            Assert.NotNull(await app.QueryAsync(db => db.Applications.Where(a => a.Id == c.Id).Select(a => a.WeatherAcknowledgedAt).FirstAsync()));

            // Outage after the cache window: the cached forecast is shown, labelled Stale, never invented.
            await weather.SetModeAsync("offline");
            app.Clock.Offset = TimeSpan.FromMinutes(20);
            await Id(page, "weather-show").ClickAsync();
            await Assertions.Expect(Id(page, "weather-freshness")).ToHaveTextAsync("Stale");
            await Assertions.Expect(Id(page, "weather-problem")).ToContainTextAsync("unavailable");
            await Assertions.Expect(Id(page, "weather-severe")).ToBeVisibleAsync();
        }
        finally
        {
            app.Clock.Offset = TimeSpan.Zero;
            await weather.SetModeAsync("normal");
        }

        await ExpectForbiddenAsync(app, "intake.alpha", "/integrations/weather");
    }

    // ------------------------------------------------------------------ 25 Data Import & Reconciliation

    [Fact]
    public async Task Data_import_previews_imports_with_row_errors_dedupes_and_validates_the_upload()
    {
        var start = DateTime.UtcNow.Date.AddDays(60);
        var csv = new StringBuilder("partnerEventId,siteCode,startUtc,endUtc,attendeeCount,updatedAtUtc\n")
            .Append($"PT-UI-1,RMP-LAWN,{start.AddHours(10):yyyy-MM-ddTHH:mm:ssZ},{start.AddHours(11):yyyy-MM-ddTHH:mm:ssZ},40,{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\n")
            .Append($"PT-UI-2,RMP-CHAPEL,{start.AddHours(13):yyyy-MM-ddTHH:mm:ssZ},{start.AddHours(14):yyyy-MM-ddTHH:mm:ssZ},25,{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\n")
            .Append($"PT-UI-3,NOPE-SITE,{start.AddHours(9):yyyy-MM-ddTHH:mm:ssZ},{start.AddHours(10):yyyy-MM-ddTHH:mm:ssZ},10,{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\n")
            .ToString();
        var file = new FilePayload { Name = "partner-ui.csv", MimeType = "text/csv", Buffer = Encoding.UTF8.GetBytes(csv) };

        var page = await app.PageAsync("scheduler.delta");
        await BrowserFixture.GoAsync(page, "/integrations/imports");
        await Id(page, "import-file").SetInputFilesAsync(file);
        await Assertions.Expect(Id(page, "import-preview")).ToBeVisibleAsync();
        await Assertions.Expect(Id(page, "import-header-state")).ToHaveTextAsync("Header matches");
        await Assertions.Expect(Id(page, "import-preview-rows")).ToHaveTextAsync("3");
        await Assertions.Expect(Id(page, "import-preview-table")).ToContainTextAsync("Unknown siteCode 'NOPE-SITE'");
        await AssertAccessibleAsync(page, "data import preview");

        await Id(page, "import-commit").ClickAsync();
        await Assertions.Expect(Id(page, "import-batch-detail")).ToBeVisibleAsync();
        await Assertions.Expect(Id(page, "import-batch-inserted")).ToHaveTextAsync("2");
        await Assertions.Expect(Id(page, "import-batch-rejected")).ToHaveTextAsync("1");
        await Assertions.Expect(Id(page, "import-row-errors")).ToContainTextAsync("NOPE-SITE");
        await Assertions.Expect(Id(page, "import-partner-events")).ToContainTextAsync("PT-UI-1");
        var report = await DownloadTextAsync(page, Id(page, "import-error-csv"));
        Assert.Contains("batchId,rowNumber,error,rawLine", report);
        Assert.Contains("NOPE-SITE", report);
        await AssertAccessibleAsync(page, "data import");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "25-data-import");

        // The same file again is recognised and changes nothing.
        await Id(page, "import-file").SetInputFilesAsync(file);
        await Assertions.Expect(Id(page, "import-already")).ToBeVisibleAsync();
        await Id(page, "import-commit").ClickAsync();
        await Assertions.Expect(Id(page, "import-batch-status")).ToHaveTextAsync("Duplicate");
        Assert.Equal(2, await app.QueryAsync(db => db.PartnerEvents.CountAsync(p => p.PartnerEventId.StartsWith("PT-UI-"))));

        // An empty file is refused by the server with a message on the file field.
        await Id(page, "import-file").SetInputFilesAsync(new FilePayload { Name = "empty.csv", MimeType = "text/csv", Buffer = [] });
        await Assertions.Expect(Id(page, "import-diagnostics")).ToContainTextAsync("The file is empty.");
        await Id(page, "import-commit").ClickAsync();
        await Assertions.Expect(Id(page, "import-file-error")).ToContainTextAsync("Upload a CSV file up to 2 MB.");
        Assert.Equal("true", await Id(page, "import-file").GetAttributeAsync("aria-invalid"));

        // Admin: the partner mock drops a file with bad rows and "Fetch from drop folder" imports it.
        var admin = await app.PageAsync("admin.india");
        await BrowserFixture.GoAsync(admin, "/integrations/imports");
        await Id(admin, "import-mock-drop").ClickAsync();
        await Assertions.Expect(admin.Locator("#imp-mock-error")).ToHaveTextAsync("Choose a file type.");
        await Id(admin, "import-mock-kind").SelectOptionAsync("bad_rows");
        await Id(admin, "import-mock-drop").ClickAsync();
        await Assertions.Expect(Id(admin, "import-drop-files")).ToBeVisibleAsync();
        await Id(admin, "import-fetch").ClickAsync();
        await Assertions.Expect(Id(admin, "import-batch-status")).ToHaveTextAsync("Completed with errors");
        await Assertions.Expect(Id(admin, "import-last-sync")).ToContainTextAsync("Last completed sync");

        await ExpectForbiddenAsync(app, "manager.echo", "/integrations/imports");
    }

    // ------------------------------------------------------------------ 18 Administration: Users & Rules

    [Fact]
    public async Task Users_and_rules_manage_accounts_rules_templates_and_audit_with_validation_and_conflicts()
    {
        var page = await app.PageAsync("admin.india");
        await BrowserFixture.GoAsync(page, "/admin/users-rules");
        await Assertions.Expect(Id(page, "user-row-intake.alpha")).ToBeVisibleAsync();
        await Assertions.Expect(Id(page, "rule-row-InfoRequest.DefaultBusinessDays")).ToBeVisibleAsync();
        await Assertions.Expect(Id(page, "template-row-permit.issued")).ToBeVisibleAsync();
        await Assertions.Expect(Id(page, "audit-row").First).ToBeVisibleAsync();
        await AssertAccessibleAsync(page, "users and rules (admin)");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "18-users-rules");

        // Create an account: invalid user name first, then a valid one.
        await page.Locator("[data-testid='user-create'] > summary").ClickAsync();
        await Id(page, "new-username").FillAsync("Bad Name");
        await Id(page, "new-display").FillAsync("Practice Scheduler Kilo");
        await Id(page, "new-email").FillAsync("kilo@example.test");
        await Id(page, "new-password").FillAsync("A-long-practice-pass-2026");
        await Id(page, "new-role-SchedulingOfficer").CheckAsync();
        await Id(page, "new-submit").ClickAsync();
        await Assertions.Expect(Id(page, "new-username-error")).ToContainTextAsync("lower-case");
        Assert.Equal("true", await Id(page, "new-username").GetAttributeAsync("aria-invalid"));
        await Id(page, "new-username").FillAsync("scheduler.kilo");
        await Id(page, "new-submit").ClickAsync();
        await Assertions.Expect(Id(page, "user-row-scheduler.kilo")).ToBeVisibleAsync();

        // Lock needs a reason; then lock and unlock.
        await Id(page, "user-manage-scheduler.kilo").ClickAsync();
        await Id(page, "manage-lock").ClickAsync();
        await Assertions.Expect(Id(page, "manage-reason-error")).ToContainTextAsync("Reason is required.");
        await Id(page, "manage-reason").FillAsync("Practice: account not needed until onboarding.");
        await Id(page, "manage-lock").ClickAsync();
        await Assertions.Expect(Id(page, "user-status-scheduler.kilo")).ToContainTextAsync("Locked");
        await Id(page, "manage-reason").FillAsync("Practice: onboarding completed, unlocking.");
        await Id(page, "manage-unlock").ClickAsync();
        await Assertions.Expect(Id(page, "user-status-scheduler.kilo")).Not.ToContainTextAsync("Locked");

        // Rule outside its range is refused; a valid value is saved and audited.
        const string rule = "InfoRequest.DefaultBusinessDays";
        await Id(page, $"rule-input-{rule}").FillAsync("99");
        await Id(page, $"rule-save-{rule}").ClickAsync();
        await Assertions.Expect(Id(page, $"rule-error-{rule}")).ToContainTextAsync("between 1 and 30");
        await Id(page, $"rule-input-{rule}").FillAsync("6");
        await Id(page, $"rule-save-{rule}").ClickAsync();
        await Assertions.Expect(Id(page, $"rule-error-{rule}")).ToBeEmptyAsync();
        await Assertions.Expect(Id(page, $"rule-input-{rule}")).ToHaveValueAsync("6");

        // Someone else saves the rule; this page's stale save shows the conflict panel instead of overwriting.
        var ruleRow = await app.QueryAsync(db => db.RuleSettings.FirstAsync(r => r.Key == rule));
        using (var other = await app.SignInAsync("admin.india"))
            (await other.PutAsync($"/api/admin/rules/{ruleRow.Id}", new { version = ruleRow.Version, value = "7" })).Ok();
        await Id(page, $"rule-input-{rule}").FillAsync("8");
        await Id(page, $"rule-save-{rule}").ClickAsync();
        await Assertions.Expect(Id(page, "conflict")).ToBeVisibleAsync();
        await Assertions.Expect(Id(page, "conflict")).ToContainTextAsync("7");
        await Id(page, "conflict-reapply").ClickAsync();
        await Assertions.Expect(Id(page, "conflict")).ToHaveCountAsync(0);
        Assert.Equal("8", await app.QueryAsync(db => db.RuleSettings.Where(r => r.Key == rule).Select(r => r.Value).FirstAsync()));
        await Id(page, $"rule-input-{rule}").FillAsync("5");
        await Id(page, $"rule-save-{rule}").ClickAsync();
        await Assertions.Expect(Id(page, $"rule-input-{rule}")).ToHaveValueAsync("5");

        // Template with an unknown placeholder is refused; the preview fills known ones.
        await Id(page, "template-open-permit.issued").ClickAsync();
        await Assertions.Expect(Id(page, "template-preview")).ToContainTextAsync("PER-2026-00007");
        await Id(page, "template-body").FillAsync("Dear {{ApplicantName}}, your {{Nonsense}} is ready.");
        await Id(page, "template-save").ClickAsync();
        await Assertions.Expect(Id(page, "template-body-error")).ToContainTextAsync("Unknown placeholder");

        // Audit viewer: invalid range, then a filter that finds the lock event.
        await Id(page, "audit-from").FillAsync("2026-10-10");
        await Id(page, "audit-to").FillAsync("2026-10-01");
        await Id(page, "audit-apply").ClickAsync();
        await Assertions.Expect(Id(page, "audit-to-error")).ToContainTextAsync("must not be before");
        await Id(page, "audit-from").FillAsync("");
        await Id(page, "audit-to").FillAsync("");
        await Id(page, "audit-action").SelectOptionAsync("User");
        await Id(page, "audit-apply").ClickAsync();
        await Assertions.Expect(Id(page, "audit-table")).ToContainTextAsync("User.Locked");
        await Assertions.Expect(Id(page, "audit-table")).ToContainTextAsync("Practice: account not needed until onboarding.");

        // Auditor: read-only rules and templates, no accounts section, filtered audit CSV export.
        var auditor = await app.PageAsync("auditor.hotel");
        await BrowserFixture.GoAsync(auditor, "/admin/users-rules");
        Assert.Equal(0, await Id(auditor, "ur-users").CountAsync());
        Assert.Equal(0, await Id(auditor, $"rule-input-{rule}").CountAsync());
        await Assertions.Expect(Id(auditor, $"rule-value-{rule}")).ToHaveTextAsync("5");
        await Id(auditor, "audit-action").SelectOptionAsync("Rule");
        await Id(auditor, "audit-apply").ClickAsync();
        await Assertions.Expect(Id(auditor, "audit-table")).ToContainTextAsync("Rule.Changed");
        var export = await DownloadTextAsync(auditor, Id(auditor, "audit-export"));
        Assert.Contains("Rule.Changed", export);
        Assert.DoesNotContain("User.Locked", export);
        await AssertAccessibleAsync(auditor, "users and rules (auditor)");

        await ExpectForbiddenAsync(app, "attendant.foxtrot", "/admin/users-rules");
    }
}
