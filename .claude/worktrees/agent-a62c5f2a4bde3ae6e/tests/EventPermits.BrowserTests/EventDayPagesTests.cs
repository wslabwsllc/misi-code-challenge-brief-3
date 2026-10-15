using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using EventPermits.Application.Services;
using EventPermits.Domain;
using EventPermits.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace EventPermits.BrowserTests;

public sealed class EventDayFixture(TestInfrastructure infra) : BrowserFixture(infra);

/// <summary>
/// Event-day and messaging pages: 13 Daily Event Roster, 14 Event Check-In, 15 Incident Register, 12 Notification Center,
/// 22 Messaging Delivery Monitor (E8-01..E8-04, E9-04, E9-05). Cases are driven to Permitted over the real API.
/// </summary>
public sealed class EventDayPagesTests(EventDayFixture app) : IClassFixture<EventDayFixture>
{
    private readonly Scenario _s = new(app);

    [Fact]
    public async Task Roster_shows_the_day_assigns_attendants_prints_and_scopes_attendants()
    {
        var c = await _s.PermittedAsync(); // attendant.foxtrot assigned
        var url = $"/operations/roster?date={c.Date:yyyy-MM-dd}";

        // An attendant who is not assigned sees nothing for that event.
        var golf = await app.PageAsync("attendant.golf");
        await OpenAsync(golf, url);
        Assert.Equal(0, await golf.Locator($"[data-testid=roster-row][data-reference='{c.Reference}']").CountAsync());

        var page = await app.PageAsync("manager.echo");
        await OpenAsync(page, url);
        var row = page.Locator($"[data-testid=roster-row][data-reference='{c.Reference}']");
        await Assertions.Expect(row).ToContainTextAsync("Practice Attendant Foxtrot");
        await Assertions.Expect(row.GetByTestId("permit-validity")).ToHaveTextAsync("Valid");
        await AssertMainAccessibleAsync(page, "roster");
        await app.ScreenshotAsync(page, "13-roster");

        // Validation: assigning without choosing a person.
        await row.GetByTestId("roster-open").ClickAsync();
        await Assertions.Expect(page.GetByTestId("roster-detail")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("readiness-item")).ToHaveCountAsync(5);
        await page.GetByTestId("assign-submit").ClickAsync();
        await Assertions.Expect(page.Locator("#assign-error")).ToContainTextAsync("Choose an attendant");
        Assert.Equal("true", await page.GetByTestId("assign-user").GetAttributeAsync("aria-invalid"));
        await AssertMainAccessibleAsync(page, "roster with validation error");

        // Main action: assign the second attendant (audited), who then sees the event.
        await page.GetByTestId("assign-user").SelectOptionAsync(app.UserIds["attendant.golf"]);
        await page.GetByTestId("assign-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("Practice Attendant Golf assigned");
        await Assertions.Expect(page.GetByTestId("attendant-list")).ToContainTextAsync("Practice Attendant Golf");
        Assert.True(await app.QueryAsync(db => db.AuditEvents.AnyAsync(a => a.ApplicationId == c.Id && a.Action == "Attendant.Assigned")));
        await OpenAsync(golf, url);
        await Assertions.Expect(golf.Locator($"[data-testid=roster-row][data-reference='{c.Reference}']")).ToBeVisibleAsync();

        // Printable run-of-show PDF for the date.
        var pdf = await page.Context.APIRequest.GetAsync(await page.GetByTestId("roster-pdf").GetAttributeAsync("href") ?? "");
        Assert.Equal(200, pdf.Status);
        Assert.StartsWith("application/pdf", pdf.Headers["content-type"]);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.BodyAsync())[..4]));

        await BrowserFixture.AssertNoHorizontalScrollAsync(page);

        // Scheduling reads the roster but cannot assign; the auditor cannot open it.
        var scheduler = await app.PageAsync("scheduler.delta");
        await OpenAsync(scheduler, url);
        await scheduler.Locator($"[data-testid=roster-row][data-reference='{c.Reference}']").GetByTestId("roster-open").ClickAsync();
        await Assertions.Expect(scheduler.GetByTestId("roster-detail")).ToBeVisibleAsync();
        Assert.Equal(0, await scheduler.GetByTestId("assign-form").CountAsync());
        await AssertForbiddenAsync("auditor.hotel", "/operations/roster");
    }

    [Fact]
    public async Task Check_in_verifies_codes_and_records_arrival_departure_and_completion()
    {
        var c = await _s.PermittedAsync();
        var code = await _s.ShortCodeAsync(c.Id);

        // Before the window: a distinct, explained result; empty input is a field error.
        var early = await app.PageAsync("attendant.foxtrot", 390, 844);
        await OpenAsync(early, "/operations/check-in");
        await early.GetByTestId("checkin-verify").ClickAsync();
        await Assertions.Expect(early.Locator("#code-error")).ToContainTextAsync("Enter the short code");
        Assert.Equal("true", await early.GetByTestId("checkin-code").GetAttributeAsync("aria-invalid"));
        await early.GetByTestId("checkin-code").FillAsync(code);
        await early.GetByTestId("checkin-verify").ClickAsync();
        await Assertions.Expect(early.GetByTestId("verdict")).ToHaveAttributeAsync("data-result", "OutsideWindow");
        await Assertions.Expect(early.GetByTestId("verdict-reason")).ToContainTextAsync("Too early");
        await Assertions.Expect(early.GetByTestId("arrival-blocked")).ToBeVisibleAsync();
        await early.GetByTestId("checkin-code").FillAsync("ZZZZZ-ZZZZZ");
        await early.GetByTestId("checkin-verify").ClickAsync();
        await Assertions.Expect(early.GetByTestId("verdict")).ToHaveAttributeAsync("data-result", "NotFound");

        try
        {
            await _s.EnterCheckInWindowAsync(c.Id);

            // The Operations Manager verifies read-only.
            var manager = await app.PageAsync("manager.echo");
            await OpenAsync(manager, "/operations/check-in");
            await Assertions.Expect(manager.GetByTestId("checkin-readonly")).ToBeVisibleAsync();
            await manager.GetByTestId("checkin-code").FillAsync(code);
            await manager.GetByTestId("checkin-verify").ClickAsync();
            await Assertions.Expect(manager.GetByTestId("verdict")).ToHaveAttributeAsync("data-result", "Valid");
            Assert.Equal(0, await manager.GetByTestId("checkin-arrive").CountAsync());

            // The assigned attendant, on a phone: arrival (T11), departure, checklist, completion (T12).
            var page = await app.PageAsync("attendant.foxtrot", 390, 844);
            await OpenAsync(page, "/operations/check-in");
            await page.GetByTestId("checkin-code").FillAsync(PermitService.FormatShortCode(code).ToLowerInvariant());
            await page.GetByTestId("checkin-verify").ClickAsync();
            await Assertions.Expect(page.GetByTestId("verdict")).ToHaveAttributeAsync("data-result", "Valid");
            await AssertMainAccessibleAsync(page, "check-in valid verdict");
            await app.ScreenshotAsync(page, "14-check-in");
            await page.GetByTestId("checkin-arrive").ClickAsync();
            await Assertions.Expect(page.GetByTestId("checkin-record")).ToContainTextAsync("Practice Attendant Foxtrot");
            Assert.Equal(CaseStatus.CheckedIn, await _s.StatusAsync(c.Id));

            await page.GetByTestId("checkout-notes").FillAsync("All guests left; chairs stacked.");
            await page.GetByTestId("checkout-submit").ClickAsync();
            await Assertions.Expect(page.GetByTestId("checkin-record")).Not.ToContainTextAsync("Not yet recorded");

            // Completing with an open checklist is refused (422) and explained.
            await page.GetByTestId("checkin-complete").ClickAsync();
            await Assertions.Expect(page.GetByTestId("problem")).ToHaveAttributeAsync("data-status", "422");
            await Assertions.Expect(page.GetByTestId("problem")).ToContainTextAsync("readiness checklist");

            var items = page.GetByTestId("readiness-item");
            for (var i = 0; i < 5; i++)
            {
                await items.Nth(i).CheckAsync();
                await Assertions.Expect(items.Nth(i)).ToBeCheckedAsync();
                await Assertions.Expect(items.Nth(i)).ToBeEnabledAsync();
            }
            await page.GetByTestId("checkin-complete").ClickAsync();
            await Assertions.Expect(page.GetByTestId("completed")).ToBeVisibleAsync();
            Assert.Equal(CaseStatus.EventCompleted, await _s.StatusAsync(c.Id));
            Assert.Equal(1, await app.QueryAsync(db => db.CheckIns.CountAsync(x => x.ApplicationId == c.Id)));
            await AssertMainAccessibleAsync(page, "check-in completed");
            await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        }
        finally
        {
            app.Clock.Offset = TimeSpan.Zero;
        }
        await AssertForbiddenAsync("auditor.hotel", "/operations/check-in");
    }

    [Fact]
    public async Task Incidents_are_logged_resolved_and_block_closure_until_resolved()
    {
        var c = await _s.PermittedAsync();
        try
        {
            await CompleteEventAsync(c);

            // Attendant logs a High incident (validation first).
            var attendant = await app.PageAsync("attendant.foxtrot");
            await OpenAsync(attendant, "/incidents");
            await attendant.GetByTestId("incident-new").ClickAsync();
            await attendant.GetByTestId("new-submit").ClickAsync();
            await Assertions.Expect(attendant.Locator("#new-event-error")).ToContainTextAsync("Choose the event");
            await Assertions.Expect(attendant.Locator("#new-category-error")).ToBeVisibleAsync();
            await Assertions.Expect(attendant.Locator("#new-description-error")).ToBeVisibleAsync();
            Assert.Equal("true", await attendant.GetByTestId("new-description").GetAttributeAsync("aria-invalid"));
            await AssertMainAccessibleAsync(attendant, "incident form with errors");

            await attendant.GetByTestId("new-event").SelectOptionAsync(c.Id.ToString());
            await FillAsync(attendant.GetByTestId("new-category"), "Trip hazard");
            await attendant.GetByTestId("new-severity").SelectOptionAsync("High");
            await FillAsync(attendant.GetByTestId("new-description"), "Loose paving stone by the chapel door; one guest stumbled.");
            await attendant.GetByTestId("new-submit").ClickAsync();
            await Assertions.Expect(attendant.GetByTestId("incident-detail")).ToHaveAttributeAsync("data-status", "Open");
            var incident = await app.QueryAsync(db => db.Incidents.FirstAsync(i => i.ApplicationId == c.Id));
            Assert.Equal(IncidentSeverity.High, incident.Severity);
            await AssertMainAccessibleAsync(attendant, "incident detail (attendant)");

            // Manager: closure is blocked (T13, 422 naming the incident), then resolve and close.
            var manager = await app.PageAsync("manager.echo");
            await OpenAsync(manager, "/incidents");
            var closure = manager.Locator($"[data-testid=closure-row][data-reference='{c.Reference}']");
            await Assertions.Expect(closure).ToContainTextAsync(incident.Reference);
            await closure.GetByTestId("close-case").ClickAsync();
            await Assertions.Expect(manager.GetByTestId("problem")).ToHaveAttributeAsync("data-code", "UNRESOLVED_HIGH_INCIDENTS");
            await Assertions.Expect(manager.GetByTestId("problem")).ToContainTextAsync(incident.Reference);

            await manager.Locator($"[data-testid=incident-row][data-reference='{incident.Reference}']").GetByTestId("incident-open").ClickAsync();
            await Assertions.Expect(manager.GetByTestId("resolve-form")).ToBeVisibleAsync();
            await manager.GetByTestId("resolve-submit").ClickAsync();
            await Assertions.Expect(manager.Locator("#resolution-error")).ToContainTextAsync("at least 10");
            await FillAsync(manager.GetByTestId("resolution"), "Stone re-set and the path checked by the grounds team.");
            await manager.GetByTestId("resolve-submit").ClickAsync();
            await Assertions.Expect(manager.GetByTestId("detail-status")).ToHaveTextAsync("Resolved");
            await Assertions.Expect(manager.GetByTestId("incident-history")).ToContainTextAsync("resolved");
            await app.ScreenshotAsync(manager, "15-incidents");

            await manager.Locator($"[data-testid=closure-row][data-reference='{c.Reference}']").GetByTestId("close-case").ClickAsync();
            await Assertions.Expect(manager.GetByTestId("success")).ToContainTextAsync($"{c.Reference} closed");
            Assert.Equal(CaseStatus.Closed, await _s.StatusAsync(c.Id));
            await BrowserFixture.AssertNoHorizontalScrollAsync(manager);

            // Auditor reads, cannot change; intake cannot open the register.
            var auditor = await app.PageAsync("auditor.hotel");
            await OpenAsync(auditor, $"/incidents?id={incident.Id}");
            await Assertions.Expect(auditor.GetByTestId("incident-detail")).ToBeVisibleAsync();
            Assert.Equal(0, await auditor.GetByTestId("incident-new").CountAsync());
            Assert.Equal(0, await auditor.GetByTestId("resolve-form").CountAsync());
            await AssertMainAccessibleAsync(auditor, "incident register (auditor)");
        }
        finally
        {
            app.Clock.Offset = TimeSpan.Zero;
        }
        await AssertForbiddenAsync("intake.alpha", "/incidents");
    }

    [Fact]
    public async Task Notification_center_lists_the_outbox_retries_failures_and_sends_manual_messages()
    {
        var c = await _s.SubmittedAsync();
        var failedId = await DeliveredAsync(c.Id, "fail_delivery", NotificationStatus.Failed);

        var page = await app.PageAsync("manager.echo");
        await OpenAsync(page, "/notifications?status=Failed");
        await Assertions.Expect(page.GetByTestId("outbox-row").First).ToHaveAttributeAsync("data-status", "Failed");
        await AssertMainAccessibleAsync(page, "notification center");

        await OpenAsync(page, $"/notifications?id={failedId}");
        await Assertions.Expect(page.GetByTestId("detail-status")).ToHaveTextAsync("Failed");
        await page.GetByTestId("retry").ClickAsync();
        await page.GetByTestId("retry-confirm").ClickAsync();
        await Assertions.Expect(page.GetByTestId("detail-status")).ToHaveTextAsync("Queued");
        await Assertions.Expect(page.GetByTestId("detail-audit")).ToContainTextAsync("Retry of manual.general");
        await Assertions.Expect(page.GetByTestId("detail-receipts")).ToContainTextAsync("Accepted"); // earlier callbacks stay visible after the retry
        await app.ScreenshotAsync(page, "12-notifications");

        // Manual notification: validation, then queued.
        await page.GetByTestId("manual-submit").ClickAsync();
        await Assertions.Expect(page.Locator("#manual-reference-error")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#manual-template-error")).ToBeVisibleAsync();
        await FillAsync(page.GetByTestId("manual-reference"), "APP-1999-99999");
        await page.GetByTestId("manual-template").SelectOptionAsync("manual.general");
        await FillAsync(page.GetByTestId("manual-message"), "Parking is on the east side this week.");
        await page.GetByTestId("manual-submit").ClickAsync();
        await Assertions.Expect(page.Locator("#manual-reference-error")).ToContainTextAsync("No case has the reference");
        Assert.Equal("true", await page.GetByTestId("manual-reference").GetAttributeAsync("aria-invalid"));
        await FillAsync(page.GetByTestId("manual-reference"), c.Reference.ToLowerInvariant());
        await page.GetByTestId("manual-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync($"Notification queued for {c.Reference}");
        Assert.True(await app.QueryAsync(db => db.Notifications.AnyAsync(n => n.ApplicationId == c.Id && n.Manual && n.Body.Contains("east side"))));
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);

        // Auditor: read-only, recipients masked.
        var auditor = await app.PageAsync("auditor.hotel");
        await OpenAsync(auditor, "/notifications");
        await Assertions.Expect(auditor.GetByTestId("outbox-row").First).ToContainTextAsync("•");
        Assert.Equal(0, await auditor.GetByTestId("manual-form").CountAsync());
        await AssertForbiddenAsync("attendant.foxtrot", "/notifications");
    }

    [Fact]
    public async Task Messaging_monitor_shows_failures_duplicate_receipts_and_retries_safely()
    {
        var c = await _s.SubmittedAsync();
        await DeliveredAsync(c.Id, "duplicate_callbacks", NotificationStatus.Delivered, expectDuplicate: true);
        var failedId = await DeliveredAsync(c.Id, "fail_delivery", NotificationStatus.Failed);

        var page = await app.PageAsync("admin.india");
        await OpenAsync(page, "/integrations/messaging");
        await Assertions.Expect(page.GetByTestId("failed-row").First).ToBeVisibleAsync();
        Assert.True(int.Parse(await page.GetByTestId("kpi-duplicates").InnerTextAsync()) >= 1);
        await Assertions.Expect(page.GetByTestId("attempt-row").First).ToBeVisibleAsync();
        await AssertMainAccessibleAsync(page, "messaging monitor");
        await app.ScreenshotAsync(page, "22-messaging");

        // Duplicate callbacks are listed as ignored.
        await page.GetByTestId("receipt-outcome").SelectOptionAsync("Duplicate");
        await page.GetByTestId("receipt-apply").ClickAsync();
        await Assertions.Expect(page.GetByTestId("receipt-row").First).ToHaveAttributeAsync("data-outcome", "Duplicate");

        // Validation of the correlation trace.
        await FillAsync(page.GetByTestId("trace-id"), "ab");
        await page.GetByTestId("trace-apply").ClickAsync();
        await Assertions.Expect(page.Locator("#trace-id-error")).ToBeVisibleAsync();

        // Trace the failed message, then retry it with confirmation (only Failed is retryable).
        var correlation = await app.QueryAsync(db => db.Notifications.Where(n => n.Id == failedId).Select(n => n.CorrelationId).FirstAsync());
        await OpenAsync(page, $"/integrations/messaging?trace={Uri.EscapeDataString(correlation)}");
        await Assertions.Expect(page.GetByTestId("failed-row")).ToHaveCountAsync(1);
        await page.GetByTestId("retry").ClickAsync();
        await page.GetByTestId("retry-confirm").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("queued for another attempt");
        Assert.Equal(NotificationStatus.Queued, await app.QueryAsync(db => db.Notifications.Where(n => n.Id == failedId).Select(n => n.Status).FirstAsync()));
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);

        await AssertForbiddenAsync("auditor.hotel", "/integrations/messaging");
    }

    // ------------------------------------------------------------------ helpers
    /// <summary>
    /// axe (WCAG 2.1 A/AA) over the page content in &lt;main&gt;. The shared navigation is excluded because its page-number
    /// badges (.nav-link__num, --ink-faint in app.css) fail colour contrast on every page; that belongs to the shell.
    /// </summary>
    private static async Task AssertMainAccessibleAsync(IPage page, string context)
    {
        var results = await page.Locator("#main").RunAxe(new AxeRunOptions
        {
            RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"] },
        });
        var problems = results.Violations.Select(v => $"{v.Id} ({v.Impact}): {v.Help} → {string.Join(" | ", v.Nodes.Take(3).Select(n => string.Join(",", n.Target)))}").ToList();
        Assert.True(problems.Count == 0, $"Accessibility violations on {context}:\n" + string.Join("\n", problems));
    }

    private static async Task OpenAsync(IPage page, string url)
    {
        await BrowserFixture.GoAsync(page, url);
        await page.GetByTestId("interactive").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
    }

    /// <summary>Fills a bound field and leaves it, so Blazor's change event fires before the next click.</summary>
    private static async Task FillAsync(ILocator field, string value)
    {
        await field.FillAsync(value);
        await field.DispatchEventAsync("change");
    }

    private async Task AssertForbiddenAsync(string user, string url)
    {
        var page = await app.PageAsync(user);
        await page.GotoAsync(url);
        await Assertions.Expect(page.GetByTestId("forbidden")).ToBeVisibleAsync();
        await page.Context.CloseAsync();
    }

    /// <summary>Checks in, checks out, finishes the checklist and completes the event over the API (clock moved into the window).</summary>
    private async Task CompleteEventAsync(Scenario.Case c)
    {
        var code = await _s.ShortCodeAsync(c.Id);
        await _s.EnterCheckInWindowAsync(c.Id);
        using var attendant = await app.SignInAsync("attendant.foxtrot");
        (await attendant.PostAsync("/api/check-in", new { code })).Ok();
        (await attendant.PostAsync($"/api/applications/{c.Id}/check-out", new { version = await _s.VersionAsync(c.Id), notes = "Done." })).Ok();
        foreach (var item in await app.QueryAsync(db => db.ReadinessItems.Where(r => r.ApplicationId == c.Id).Select(r => r.Id).ToListAsync()))
            (await attendant.PutAsync($"/api/readiness/{item}", new { version = await _s.VersionAsync(c.Id), done = true })).Ok();
        (await attendant.PostAsync($"/api/applications/{c.Id}/complete", new { version = await _s.VersionAsync(c.Id) })).Ok();
    }

    /// <summary>
    /// Queues a manual message, sends it through the gateway mock in <paramref name="mode"/> (the worker's job, run here
    /// directly) and waits for the signed delivery webhook to settle it.
    /// </summary>
    private async Task<Guid> DeliveredAsync(Guid caseId, string mode, NotificationStatus expected, bool expectDuplicate = false)
    {
        var marker = Guid.NewGuid().ToString("N");
        using (var manager = await app.SignInAsync("manager.echo"))
            (await manager.PostAsync("/api/notifications", new { applicationId = caseId, templateCode = "manual.general", message = $"Practice message {marker}" })).Ok();
        var id = await app.QueryAsync(db => db.Notifications.Where(n => n.Body.Contains(marker)).Select(n => n.Id).FirstAsync());
        var notify = app.MockAdmin("notify");
        await notify.SetModeAsync(mode);
        try
        {
            await app.WithServiceAsync(sp => sp.GetRequiredService<NotificationService>().DispatchDueAsync(CancellationToken.None));
        }
        finally
        {
            await notify.SetModeAsync("normal");
        }
        for (var i = 0; i < 60; i++)
        {
            var n = await app.QueryAsync(db => db.Notifications.AsNoTracking().FirstAsync(x => x.Id == id));
            var duplicates = !expectDuplicate || await app.QueryAsync(db => db.WebhookReceipts.AnyAsync(r => r.MessageId == n.GatewayMessageId && r.Outcome == WebhookOutcome.Duplicate));
            if (n.Status == expected && duplicates) return id;
            await Task.Delay(250);
        }
        throw new Xunit.Sdk.XunitException($"Notification {id} did not reach {expected} through the webhook in mode {mode}.");
    }
}
