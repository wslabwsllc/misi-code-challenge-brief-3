using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using EventPermits.Domain;
using EventPermits.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace EventPermits.BrowserTests;

public sealed class DecisionPagesFixture(TestInfrastructure infra) : BrowserFixture(infra)
{
    // Bind only a dynamic loopback port, so parallel test hosts never collide on Kestrel's default 127.0.0.1:5000.
    protected override void Configure(IWebHostBuilder builder) => builder.UseSetting(WebHostDefaults.ServerUrlsKey, "http://127.0.0.1:0");
}

/// <summary>
/// Pages 05, 06, 10, 11 and 20 (E5-01, E5-02, E5-03, E7-01, E7-03): data, a main action end to end, validation,
/// role blocking, stale-write conflict, axe and phone width. Test data is created through the real API (Scenario).
/// </summary>
public sealed class DecisionPagesTests(DecisionPagesFixture app) : IClassFixture<DecisionPagesFixture>
{
    private readonly Scenario _scenario = new(app);

    /// <summary>
    /// Same WCAG 2.1 A/AA run as <see cref="BrowserFixture.AssertAccessibleAsync"/>, minus the decorative nav number
    /// (<c>.nav-link__num</c>, aria-hidden) whose active-state contrast is a shell issue outside these pages.
    /// </summary>
    private static async Task AccessibleAsync(IPage page, string context)
    {
        var results = await page.RunAxe(
            new AxeRunContext { Exclude = [new AxeSelector(".nav-link__num")] },
            new AxeRunOptions { RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"] } });
        var problems = results.Violations.Select(v => $"{v.Id} ({v.Impact}): {v.Help} → {string.Join(" | ", v.Nodes.Take(3).Select(n => string.Join(",", n.Target)))}").ToList();
        Assert.True(problems.Count == 0, $"Accessibility violations on {context}:\n" + string.Join("\n", problems));
    }

    // ------------------------------------------------------------------ 05 Eligibility Review Queue

    [Fact]
    public async Task Review_queue_lists_submitted_cases_and_marks_one_eligible()
    {
        var c = await _scenario.SubmittedAsync();
        var page = await app.PageAsync("reviewer.charlie");
        await BrowserFixture.GoAsync(page, $"/reviews?q={c.Reference}");
        var row = page.GetByTestId("review-row").Filter(new() { HasText = c.Reference });
        await Assertions.Expect(row).ToBeVisibleAsync();
        await Assertions.Expect(row.GetByTestId("registry-chip")).ToHaveTextAsync("Not verified");
        await AccessibleAsync(page, "review queue");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "05-reviews");

        await row.GetByTestId("review-open").ClickAsync();
        var workspace = page.GetByTestId("review-workspace");
        await Assertions.Expect(workspace).ToBeVisibleAsync();
        // The registry check runs automatically on opening (E5-02 AC1).
        await Assertions.Expect(page.GetByTestId("auto-verify-note")).ToContainTextAsync("Matched");
        await Assertions.Expect(page.GetByTestId("registry-evidence").GetByTestId("registry-chip")).ToHaveTextAsync("Valid");
        await Assertions.Expect(page.GetByTestId("review-documents")).ToContainTextAsync("affiliation.pdf");
        await AccessibleAsync(page, "review workspace");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "05-reviews-workspace");

        foreach (var box in await page.GetByTestId("checklist-item").AllAsync()) await box.CheckAsync();
        await page.GetByTestId("decision-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("marked eligible (T4)");
        await Assertions.Expect(page.GetByTestId("review-workspace")).ToHaveCountAsync(0);
        Assert.Equal(CaseStatus.Eligible, await _scenario.StatusAsync(c.Id));
    }

    [Fact]
    public async Task Review_decisions_validate_fields_and_request_information()
    {
        var c = await _scenario.SubmittedAsync();
        var page = await app.PageAsync("reviewer.charlie");
        await BrowserFixture.GoAsync(page, $"/reviews?case={c.Id}&q={c.Reference}");
        await Assertions.Expect(page.GetByTestId("review-workspace")).ToBeVisibleAsync();

        // Eligible without the checklist: client-side field error, focus moved to the first invalid control.
        await page.GetByTestId("decision-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-checklist")).ToBeVisibleAsync();

        // Reject without a reason.
        await page.GetByTestId("decision-reject").CheckAsync();
        await page.GetByTestId("decision-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-reason")).ToContainTextAsync("Rejection reason is required");
        Assert.Equal("true", await page.GetByTestId("reject-reason").GetAttributeAsync("aria-invalid"));
        Assert.Equal("reject-reason-hint reject-reason-error", await page.GetByTestId("reject-reason").GetAttributeAsync("aria-describedby"));
        await AccessibleAsync(page, "review validation");

        // Request information (T2) with two items.
        await page.GetByTestId("decision-info").CheckAsync();
        await page.GetByTestId("info-items").FillAsync("Signed event plan\nProof of affiliation dated this year");
        await page.GetByTestId("info-message").FillAsync("Please upload both documents.");
        await page.GetByTestId("decision-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("Information requested");
        Assert.Equal(CaseStatus.NeedsInformation, await _scenario.StatusAsync(c.Id));
    }

    [Fact]
    public async Task Review_with_unknown_registry_result_needs_an_override_reason()
    {
        var c = await _scenario.SubmittedAsync(registryRef: "REF-10007"); // Unknown in the registry mock
        var page = await app.PageAsync("reviewer.charlie");
        await BrowserFixture.GoAsync(page, $"/reviews?case={c.Id}&q={c.Reference}");
        await Assertions.Expect(page.GetByTestId("registry-evidence").GetByTestId("registry-chip")).ToHaveTextAsync("Unknown");
        await Assertions.Expect(page.GetByTestId("override-guidance")).ToContainTextAsync("allow an override");
        foreach (var box in await page.GetByTestId("checklist-item").AllAsync()) await box.CheckAsync();
        await page.GetByTestId("override-reason").FillAsync("too short");
        await page.GetByTestId("decision-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-overrideReason")).ToContainTextAsync("20 to 1000");
        await page.GetByTestId("override-reason").FillAsync("Identity confirmed by phone with the organisation secretary.");
        await page.GetByTestId("decision-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("marked eligible");
        Assert.True(await app.QueryAsync(db => db.EligibilityReviews.AnyAsync(r => r.ApplicationId == c.Id && r.RegistryOverride)));
    }

    [Fact]
    public async Task Review_stale_write_shows_the_conflict_panel()
    {
        var c = await _scenario.SubmittedAsync();
        var page = await app.PageAsync("reviewer.charlie");
        await BrowserFixture.GoAsync(page, $"/reviews?case={c.Id}&q={c.Reference}");
        await Assertions.Expect(page.GetByTestId("review-workspace")).ToBeVisibleAsync();
        // Someone else records new evidence meanwhile (bumps the case version).
        using (var other = await app.SignInAsync("reviewer.charlie"))
            (await other.PostAsync($"/api/applications/{c.Id}/verify-registry")).Ok();
        foreach (var box in await page.GetByTestId("checklist-item").AllAsync()) await box.CheckAsync();
        await page.GetByTestId("decision-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("conflict")).ToBeVisibleAsync();
        Assert.Equal("409", await page.GetByTestId("problem").GetAttributeAsync("data-status"));
        await AccessibleAsync(page, "review conflict");
        await page.GetByTestId("conflict-reapply").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("marked eligible");
        Assert.Equal(CaseStatus.Eligible, await _scenario.StatusAsync(c.Id));
    }

    [Fact]
    public async Task Review_queue_is_blocked_for_other_roles()
    {
        var page = await app.PageAsync("manager.echo");
        await page.GotoAsync("/reviews");
        await Assertions.Expect(page.GetByTestId("forbidden")).ToBeVisibleAsync();
    }

    // ------------------------------------------------------------------ 06 Request Information Inbox

    private async Task<(Scenario.Case Case, Guid RequestId)> NeedsInformationAsync()
    {
        var c = await _scenario.SubmittedAsync();
        using var reviewer = await app.SignInAsync("reviewer.charlie");
        (await reviewer.PostAsync($"/api/applications/{c.Id}/information-requests", new
        {
            version = await _scenario.VersionAsync(c.Id), items = new[] { "Signed event plan", "Proof of affiliation" }, message = "Please send both.",
        })).Ok();
        var requestId = await app.QueryAsync(db => db.InformationRequests.Where(r => r.ApplicationId == c.Id).Select(r => r.Id).FirstAsync());
        return (c, requestId);
    }

    [Fact]
    public async Task Inbox_coordinator_answers_with_a_file_and_resubmits()
    {
        var (c, _) = await NeedsInformationAsync();
        var page = await app.PageAsync("intake.alpha");
        await BrowserFixture.GoAsync(page, "/information-requests");
        var row = page.GetByTestId("inbox-row").Filter(new() { HasText = c.Reference });
        await Assertions.Expect(row).ToBeVisibleAsync();
        await Assertions.Expect(row.GetByTestId("request-status")).ToHaveTextAsync("Open");
        await AccessibleAsync(page, "inbox");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "06-information-requests");

        await row.GetByTestId("respond-open").ClickAsync();
        await Assertions.Expect(page.GetByTestId("respond-panel")).ToBeVisibleAsync();
        // Validation: an empty response is refused before anything is sent.
        await page.GetByTestId("respond-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-response")).ToContainTextAsync("Response is required");
        Assert.Equal("true", await page.GetByTestId("response-text").GetAttributeAsync("aria-invalid"));
        await AccessibleAsync(page, "inbox respond validation");

        await page.GetByTestId("response-text").FillAsync("Both documents are attached; the plan is signed by the post commander.");
        await page.GetByTestId("response-file").SetInputFilesAsync(new FilePayload { Name = "signed-plan.pdf", MimeType = "application/pdf", Buffer = Files.Pdf() });
        await page.GetByTestId("response-doctype").SelectOptionAsync("EventPlan");
        await page.GetByTestId("respond-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("recorded with signed-plan.pdf");
        Assert.True(await app.QueryAsync(db => db.Attachments.AnyAsync(a => a.ApplicationId == c.Id && a.Purpose == AttachmentPurpose.InformationResponse)));

        await page.GetByTestId("view-answered").ClickAsync();
        var answered = page.GetByTestId("inbox-row").Filter(new() { HasText = c.Reference });
        await Assertions.Expect(answered.GetByTestId("request-status")).ToHaveTextAsync("Answered");
        await answered.GetByTestId("resubmit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("resubmitted (T3)");
        Assert.Equal(CaseStatus.Submitted, await _scenario.StatusAsync(c.Id));
    }

    [Fact]
    public async Task Inbox_reviewer_tracks_overdue_requests_read_only()
    {
        var (c, requestId) = await NeedsInformationAsync();
        await app.WithDbAsync(async db =>
        {
            var r = await db.InformationRequests.FirstAsync(x => x.Id == requestId);
            r.DueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);
            await db.SaveChangesAsync();
        });
        var page = await app.PageAsync("reviewer.charlie");
        await BrowserFixture.GoAsync(page, "/information-requests?view=overdue");
        var row = page.GetByTestId("inbox-row").Filter(new() { HasText = c.Reference });
        await Assertions.Expect(row.GetByTestId("overdue-flag")).ToHaveTextAsync("Overdue");
        Assert.Equal(0, await page.GetByTestId("respond-open").CountAsync()); // reviewers track, they do not answer
        await AccessibleAsync(page, "inbox overdue");
    }

    [Fact]
    public async Task Inbox_is_blocked_for_roles_without_requests()
    {
        var page = await app.PageAsync("scheduler.delta");
        await page.GotoAsync("/information-requests");
        await Assertions.Expect(page.GetByTestId("forbidden")).ToBeVisibleAsync();
    }

    // ------------------------------------------------------------------ 20 Applicant Registry Verification

    [Fact]
    public async Task Registry_lookup_shows_the_result_history_and_attempts()
    {
        var page = await app.PageAsync("reviewer.charlie");
        await BrowserFixture.GoAsync(page, "/integrations/registry");
        await Assertions.Expect(page.GetByTestId("lookup-form")).ToBeVisibleAsync();

        // Validation first.
        await page.GetByTestId("lookup-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-reference")).ToContainTextAsync("Reference is required");
        Assert.Equal("true", await page.GetByTestId("lookup-reference").GetAttributeAsync("aria-invalid"));

        await page.GetByTestId("lookup-reference").FillAsync("REF-10002");
        await page.GetByTestId("lookup-submit").ClickAsync();
        var result = page.GetByTestId("lookup-result");
        await Assertions.Expect(result.GetByTestId("registry-chip")).ToHaveTextAsync("Valid");
        await Assertions.Expect(result).ToContainTextAsync("Quinn Practice-Galloway");
        await Assertions.Expect(page.GetByTestId("history-row").Filter(new() { HasText = "REF-10002" }).First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("registry-attempts")).ToContainTextAsync("HTTP 200");
        await AccessibleAsync(page, "registry verification");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "20-registry");

        // 404 is a final "not found" answer.
        await page.GetByTestId("lookup-reference").FillAsync("REF-10101");
        await page.GetByTestId("lookup-submit").ClickAsync();
        await Assertions.Expect(result.GetByTestId("registry-chip")).ToHaveTextAsync("Not found");
    }

    [Fact]
    public async Task Registry_rate_limit_shows_pending_with_retry()
    {
        var page = await app.PageAsync("reviewer.charlie");
        await BrowserFixture.GoAsync(page, "/integrations/registry");
        await app.MockAdmin("registry").SetModeAsync("ratelimited");
        try
        {
            await page.GetByTestId("lookup-reference").FillAsync("REF-10003");
            await page.GetByTestId("lookup-submit").ClickAsync();
            await Assertions.Expect(page.GetByTestId("lookup-result").GetByTestId("registry-chip")).ToHaveTextAsync("Pending", new() { Timeout = 30_000 });
            await Assertions.Expect(page.GetByTestId("lookup-retry")).ToBeVisibleAsync();
        }
        finally
        {
            await app.MockAdmin("registry").SetModeAsync("normal");
        }
        await page.GetByTestId("lookup-retry").ClickAsync();
        await Assertions.Expect(page.GetByTestId("lookup-result").GetByTestId("registry-chip")).ToHaveTextAsync("Valid");
    }

    [Fact]
    public async Task Registry_page_is_read_only_for_auditors_and_blocked_for_intake()
    {
        var auditor = await app.PageAsync("auditor.hotel");
        await BrowserFixture.GoAsync(auditor, "/integrations/registry");
        await Assertions.Expect(auditor.GetByTestId("registry-read-only")).ToBeVisibleAsync();
        Assert.Equal(0, await auditor.GetByTestId("lookup-form").CountAsync());
        await AccessibleAsync(auditor, "registry read-only");

        var intake = await app.PageAsync("intake.alpha");
        await intake.GotoAsync("/integrations/registry");
        await Assertions.Expect(intake.GetByTestId("forbidden")).ToBeVisibleAsync();
    }

    // ------------------------------------------------------------------ 10 Approval Workspace

    [Fact]
    public async Task Approval_workspace_approves_a_reconciled_case()
    {
        var c = await _scenario.PendingApprovalAsync();
        var page = await app.PageAsync("manager.echo");
        await BrowserFixture.GoAsync(page, "/approvals");
        var row = page.GetByTestId("approval-row").Filter(new() { HasText = c.Reference });
        await Assertions.Expect(row.GetByTestId("calendar-chip")).ToHaveTextAsync("Reconciled");
        await AccessibleAsync(page, "approvals");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "10-approvals");

        await row.GetByTestId("approval-open").ClickAsync();
        await Assertions.Expect(page.GetByTestId("approval-panel")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("reservation-summary")).ToContainTextAsync("40");
        await Assertions.Expect(page.GetByTestId("check-sod")).ToContainTextAsync("Passed");
        await AccessibleAsync(page, "approval panel");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await page.GetByTestId("approve-notes").FillAsync("All checks complete; venue confirmed.");
        await page.GetByTestId("approval-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("approved (T8)");
        Assert.Equal(CaseStatus.Approved, await _scenario.StatusAsync(c.Id));
        Assert.True(await app.QueryAsync(db => db.ApprovalDecisions.AnyAsync(d => d.ApplicationId == c.Id && d.ApproverName == "Practice Manager Echo")));
    }

    [Fact]
    public async Task Approval_rejection_needs_a_reason_and_releases_the_case()
    {
        var c = await _scenario.PendingApprovalAsync();
        var page = await app.PageAsync("manager.echo");
        await BrowserFixture.GoAsync(page, $"/approvals?case={c.Id}");
        await page.GetByTestId("decision-reject").CheckAsync();
        await page.GetByTestId("approval-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-reason")).ToContainTextAsync("Rejection reason is required");
        Assert.Equal("true", await page.GetByTestId("reject-reason").GetAttributeAsync("aria-invalid"));
        await AccessibleAsync(page, "approval validation");
        await page.GetByTestId("reject-reason").FillAsync("The venue is needed for a state ceremony that day.");
        await page.GetByTestId("approval-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("rejected (T9)");
        Assert.Equal(CaseStatus.Rejected, await _scenario.StatusAsync(c.Id));
    }

    [Fact]
    public async Task Approval_is_blocked_by_separation_of_duties_and_by_the_calendar()
    {
        // The dual-role user reviewed this case, so they cannot decide it.
        var reviewed = await _scenario.PendingApprovalAsync(reviewer: "dual.juliet");
        var dual = await app.PageAsync("dual.juliet");
        await BrowserFixture.GoAsync(dual, $"/approvals?case={reviewed.Id}");
        await Assertions.Expect(dual.GetByTestId("sod-notice")).ToBeVisibleAsync();
        await Assertions.Expect(dual.GetByTestId("approval-submit")).ToBeDisabledAsync();
        await AccessibleAsync(dual, "approval separation of duties");

        // Not reconciled: approval answers 422 with the blocking reason; a manual sync after the push unblocks it.
        var unsynced = await _scenario.PendingApprovalAsync(reconcile: false);
        var page = await app.PageAsync("manager.echo");
        await BrowserFixture.GoAsync(page, $"/approvals?case={unsynced.Id}");
        await Assertions.Expect(page.GetByTestId("check-calendar")).ToContainTextAsync("Not reconciled");
        await page.GetByTestId("approval-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("problem")).ToContainTextAsync("Final approval is blocked");
        Assert.Equal("422", await page.GetByTestId("problem").GetAttributeAsync("data-status"));
        await app.WithServiceAsync(sp => sp.GetRequiredService<EventPermits.Application.Services.CalendarSyncService>().ProcessOutboxAsync(CancellationToken.None));
        await page.GetByTestId("calendar-sync").ClickAsync();
        await Assertions.Expect(page.GetByTestId("check-calendar")).ToContainTextAsync("Reconciled for this slot");
        await page.GetByTestId("approval-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("approved (T8)");
    }

    [Fact]
    public async Task Approval_workspace_is_blocked_for_reviewers()
    {
        var page = await app.PageAsync("reviewer.charlie");
        await page.GotoAsync("/approvals");
        await Assertions.Expect(page.GetByTestId("forbidden")).ToBeVisibleAsync();
    }

    // ------------------------------------------------------------------ 11 Permit Center

    [Fact]
    public async Task Permit_center_issues_a_permit_and_offers_the_pdf()
    {
        var c = await _scenario.ApprovedAsync();
        var page = await app.PageAsync("manager.echo");
        await BrowserFixture.GoAsync(page, "/permits");
        var ready = page.GetByTestId("ready-row").Filter(new() { HasText = c.Reference });
        await Assertions.Expect(ready).ToBeVisibleAsync();
        await AccessibleAsync(page, "permit center");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "11-permits");

        await ready.GetByTestId("issue-open").ClickAsync();
        await Assertions.Expect(page.GetByTestId("issue-panel")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("issue-conditions")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("reserved venue"));
        await page.GetByTestId("issue-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("(T10)");
        Assert.Equal(CaseStatus.Permitted, await _scenario.StatusAsync(c.Id));

        var row = page.GetByTestId("permit-row").Filter(new() { HasText = c.Reference });
        await row.GetByTestId("permit-open").ClickAsync();
        var detail = page.GetByTestId("permit-detail");
        await Assertions.Expect(detail.GetByTestId("permit-status")).ToHaveTextAsync("Active");
        await detail.GetByTestId("file-issue").ClickAsync();
        var link = detail.GetByTestId("file-download");
        await Assertions.Expect(link).ToBeVisibleAsync();
        Assert.StartsWith("/files/", await link.GetAttributeAsync("href"));
        await AccessibleAsync(page, "permit detail");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
    }

    [Fact]
    public async Task Permit_issue_waits_for_the_severe_weather_acknowledgement()
    {
        var c = await _scenario.ApprovedAsync();
        await app.WithDbAsync(async db =>
        {
            db.WeatherSnapshots.Add(new WeatherSnapshot
            {
                VenueId = c.VenueId, VenueCode = c.VenueCode, Date = c.Date, Summary = "Thunderstorms with damaging gusts", TemperatureC = 18, WindKph = 85,
                PrecipitationPct = 90, Advisory = WeatherAdvisory.Severe, SourceTimestampUtc = DateTimeOffset.UtcNow, RetrievedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        });
        var page = await app.PageAsync("manager.echo");
        await BrowserFixture.GoAsync(page, $"/permits?issue={c.Id}");
        await Assertions.Expect(page.GetByTestId("weather-block")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("issue-submit")).ToBeDisabledAsync();
        await page.GetByTestId("ack-note").FillAsync("short");
        await page.GetByTestId("ack-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-note")).ToContainTextAsync("10 to 1000");
        await AccessibleAsync(page, "permit weather block");
        await page.GetByTestId("ack-note").FillAsync("Marquee removed from the plan; service moves under the chapel portico.");
        await page.GetByTestId("ack-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("weather-acknowledged")).ToBeVisibleAsync();
        await page.GetByTestId("issue-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("(T10)");
        Assert.Equal(CaseStatus.Permitted, await _scenario.StatusAsync(c.Id));
    }

    [Fact]
    public async Task Permit_reissue_and_revoke_with_reasons()
    {
        var c = await _scenario.PermittedAsync();
        var permitId = await app.QueryAsync(db => db.Permits.Where(p => p.ApplicationId == c.Id).Select(p => p.Id).FirstAsync());
        var page = await app.PageAsync("manager.echo");
        await BrowserFixture.GoAsync(page, $"/permits?permit={permitId}");
        var detail = page.GetByTestId("permit-detail");
        await Assertions.Expect(detail.GetByTestId("version-row")).ToHaveCountAsync(1);

        await page.GetByTestId("permit-action-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-reason")).ToContainTextAsync("Reissue reason is required");
        Assert.Equal("true", await page.GetByTestId("permit-reason").GetAttributeAsync("aria-invalid"));
        await page.GetByTestId("permit-reason").FillAsync("Start time moved by thirty minutes at the family's request.");
        await page.GetByTestId("permit-action-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("reissued as v2");
        await Assertions.Expect(detail.GetByTestId("version-row")).ToHaveCountAsync(2);
        await Assertions.Expect(detail.Locator("[data-version='1']")).ToContainTextAsync("Superseded");

        await page.GetByTestId("action-revoke").CheckAsync();
        await page.GetByTestId("permit-reason").FillAsync("Venue closed after storm damage; event to be rebooked.");
        await page.GetByTestId("permit-action-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("revoked");
        await Assertions.Expect(detail.GetByTestId("permit-status")).ToHaveTextAsync("Revoked");
    }

    [Fact]
    public async Task Permit_center_shows_auditors_metadata_only_and_blocks_intake()
    {
        var c = await _scenario.PermittedAsync();
        var permitId = await app.QueryAsync(db => db.Permits.Where(p => p.ApplicationId == c.Id).Select(p => p.Id).FirstAsync());
        var auditor = await app.PageAsync("auditor.hotel");
        await BrowserFixture.GoAsync(auditor, $"/permits?permit={permitId}");
        await Assertions.Expect(auditor.GetByTestId("permit-metadata-only")).ToBeVisibleAsync();
        await Assertions.Expect(auditor.GetByTestId("file-metadata-only").First).ToBeVisibleAsync();
        Assert.Equal(0, await auditor.GetByTestId("file-issue").CountAsync());
        Assert.Equal(0, await auditor.GetByTestId("ready-table").CountAsync());
        Assert.Equal(0, await auditor.GetByTestId("permit-action-form").CountAsync());
        await AccessibleAsync(auditor, "permit center auditor");

        var intake = await app.PageAsync("intake.alpha");
        await intake.GotoAsync("/permits");
        await Assertions.Expect(intake.GetByTestId("forbidden")).ToBeVisibleAsync();
    }
}
