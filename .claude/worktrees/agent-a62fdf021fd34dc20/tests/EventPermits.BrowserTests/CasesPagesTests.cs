using System.Text.RegularExpressions;
using EventPermits.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace EventPermits.BrowserTests;

public sealed class CasesPagesFixture(TestInfrastructure infra) : BrowserFixture(infra);

/// <summary>
/// Pages 02 Application Register, 03 New Application Wizard, 04 Application Detail and 23 Document Vault
/// (E4-01, E4-02, E4-03, E4-04, E3-05): real data, a main action end to end, validation, role blocking,
/// axe and phone width. Test cases are created through the HTTP API, as the UI would.
/// </summary>
public sealed class CasesPagesTests(CasesPagesFixture app) : IClassFixture<CasesPagesFixture>
{
    private readonly Scenario _s = new(app);
    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 15_000 };

    /// <summary>Opens a page and waits until it is interactive (prerendered markup is not clickable yet).</summary>
    private static async Task OpenAsync(IPage page, string url)
    {
        await BrowserFixture.GoAsync(page, url);
        await page.Locator("[data-interactive]").WaitForAsync(new() { State = WaitForSelectorState.Attached });
    }

    private static ILocator Row(IPage page, string reference) => page.Locator($"[data-testid=register-row][data-reference='{reference}']");

    // ------------------------------------------------------------------ 02 Application Register

    [Fact]
    public async Task Register_lists_cases_keeps_filters_in_the_url_and_manages_saved_views()
    {
        var submitted = await _s.SubmittedAsync();
        var draft = await _s.DraftAsync();
        var page = await app.PageAsync("reviewer.charlie");

        await OpenAsync(page, "/applications");
        await Assertions.Expect(Row(page, submitted.Reference)).ToBeVisibleAsync();
        await Assertions.Expect(Row(page, draft.Reference)).ToBeVisibleAsync();
        await BrowserFixture.AssertAccessibleAsync(page, "register");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "02-register");

        // A dashboard-style deep link restores the filter; the draft drops out.
        await OpenAsync(page, "/applications?status=Submitted");
        await Assertions.Expect(Row(page, submitted.Reference)).ToBeVisibleAsync();
        await Assertions.Expect(Row(page, draft.Reference)).ToHaveCountAsync(0);

        // Searching from the form puts the filter into the address.
        await page.GetByTestId("filter-search").FillAsync(submitted.Reference);
        await page.GetByTestId("filter-apply").ClickAsync();
        await page.WaitForURLAsync(new Regex("q=APP-"));
        await Assertions.Expect(page.GetByTestId("register-row")).ToHaveCountAsync(1);
        Assert.Contains($"q={submitted.Reference}", await page.GetByTestId("register-export").GetAttributeAsync("href"));

        // Saved views: server validation on the name, then save, rename and delete.
        await page.GetByTestId("view-name").FillAsync("x");
        await page.GetByTestId("view-save").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-name")).ToBeVisibleAsync();
        Assert.Equal("true", await page.GetByTestId("view-name").GetAttributeAsync("aria-invalid"));
        await BrowserFixture.AssertAccessibleAsync(page, "register with a validation error");

        await page.GetByTestId("view-name").FillAsync("Submitted search");
        await page.GetByTestId("view-save").ClickAsync();
        await Assertions.Expect(page.GetByTestId("saved-view")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("view-open")).ToHaveTextAsync("Submitted search");

        await page.GetByTestId("view-rename").ClickAsync();
        await page.GetByTestId("view-rename-input").FillAsync("My submitted search");
        await page.GetByTestId("view-rename-save").ClickAsync();
        await Assertions.Expect(page.GetByTestId("view-open")).ToHaveTextAsync("My submitted search");

        await OpenAsync(page, "/applications");
        await page.GetByTestId("view-open").ClickAsync();
        await page.WaitForURLAsync(new Regex("q=APP-"));
        await Assertions.Expect(page.GetByTestId("register-row")).ToHaveCountAsync(1);

        await page.GetByTestId("view-delete").ClickAsync();
        await Assertions.Expect(page.GetByTestId("views-empty")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Register_sorts_on_the_server_and_masks_applicants_for_auditors()
    {
        var c = await _s.SubmittedAsync();
        var auditor = await app.PageAsync("auditor.hotel");
        await OpenAsync(auditor, $"/applications?q={c.Reference}");
        await Assertions.Expect(Row(auditor, c.Reference)).ToContainTextAsync("A. P.");
        await Assertions.Expect(Row(auditor, c.Reference)).Not.ToContainTextAsync("Avery Practice-Alder");

        await auditor.GetByTestId("sort-reference").ClickAsync();
        await auditor.WaitForURLAsync(new Regex("sort=Reference"));
        Assert.Equal("ascending", await auditor.Locator("th[aria-sort]").GetAttributeAsync("aria-sort"));
    }

    [Fact]
    public async Task Register_is_blocked_for_roles_without_case_access()
    {
        var admin = await app.PageAsync("admin.india");
        await OpenAsync(admin, "/applications");
        await Assertions.Expect(admin.GetByTestId("forbidden")).ToBeVisibleAsync();
        var attendant = await app.PageAsync("attendant.golf");
        await OpenAsync(attendant, "/applications");
        await Assertions.Expect(attendant.GetByTestId("forbidden")).ToBeVisibleAsync();
    }

    // ------------------------------------------------------------------ 03 New Application Wizard

    [Fact]
    public async Task Wizard_autosaves_a_draft_validates_each_step_and_submits()
    {
        var (date, start) = Scenario.NextSlot();
        var venueId = await _s.VenueIdAsync("RMP-CHAPEL");
        var page = await app.PageAsync("intake.bravo");
        await OpenAsync(page, "/applications/new");
        await BrowserFixture.AssertAccessibleAsync(page, "wizard step 1");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "03-wizard");

        // Server validation linked to the fields; focus moves to the first invalid field.
        await page.GetByTestId("wizard-next").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-fullName")).ToBeVisibleAsync(Slow);
        Assert.Equal("true", await page.GetByTestId("wizard-fullName").GetAttributeAsync("aria-invalid"));
        Assert.Contains("fullName-error", await page.GetByTestId("wizard-fullName").GetAttributeAsync("aria-describedby"));
        await BrowserFixture.AssertAccessibleAsync(page, "wizard step 1 with errors");

        // That first save created the draft (idempotent key); typing then autosaves after a short pause.
        await page.WaitForURLAsync(new Regex("/applications/new/[0-9a-f-]{36}/step/1"));
        var id = Regex.Match(page.Url, "[0-9a-f-]{36}").Value;
        await page.GetByTestId("wizard-fullName").FillAsync("Morgan Practice-Birch");
        await Assertions.Expect(page.GetByTestId("wizard-status")).ToContainTextAsync("Draft saved", new() { Timeout = 15_000 });
        Assert.Equal("Morgan Practice-Birch", await app.QueryAsync(db => db.Applications.Where(a => a.Id == Guid.Parse(id)).Select(a => a.Applicant!.FullName).FirstAsync()));

        await page.GetByTestId("wizard-email").FillAsync("morgan.birch@example.test");
        await page.GetByTestId("wizard-registryReference").FillAsync("REF-10002");
        await page.GetByTestId("wizard-next").ClickAsync();
        await page.WaitForURLAsync("**/step/2");

        await page.GetByTestId("wizard-title").FillAsync("Graveside service for a fictional veteran");
        await page.GetByTestId("wizard-categoryId").SelectOptionAsync(new SelectOptionValue { Label = "Memorial service" });
        await page.GetByTestId("wizard-venueId").SelectOptionAsync(venueId.ToString());
        await page.GetByTestId("wizard-localDate").FillAsync(date.ToString("yyyy-MM-dd"));
        await page.GetByTestId("wizard-localStart").FillAsync(start.ToString("HH:mm"));
        await page.GetByTestId("wizard-durationMinutes").FillAsync("20");
        await page.GetByTestId("wizard-attendeeCount").FillAsync("25");
        await page.GetByTestId("wizard-next").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-durationMinutes")).ToBeVisibleAsync();
        await page.GetByTestId("wizard-durationMinutes").FillAsync("60");
        await page.GetByTestId("wizard-next").ClickAsync();
        await page.WaitForURLAsync("**/step/3");

        await page.GetByTestId("wizard-specialNeeds").FillAsync("Step-free access to the chapel.");
        await page.GetByTestId("wizard-next").ClickAsync();
        await page.WaitForURLAsync("**/step/4");
        await Assertions.Expect(page.GetByTestId("wizard-problems")).ToContainTextAsync("Required documents missing");

        // A file with the wrong signature is refused next to the file field.
        await page.GetByTestId("wizard-file").SetInputFilesAsync(new FilePayload { Name = "notes.pdf", MimeType = "application/pdf", Buffer = "not a pdf"u8.ToArray() });
        await page.GetByTestId("wizard-upload").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-file")).ToContainTextAsync("not a valid PDF");

        await page.GetByTestId("wizard-doctype").SelectOptionAsync("ProofOfAffiliation");
        await page.GetByTestId("wizard-file").SetInputFilesAsync(new FilePayload { Name = "affiliation.pdf", MimeType = "application/pdf", Buffer = Files.Pdf() });
        await page.GetByTestId("wizard-upload").ClickAsync();
        await Assertions.Expect(page.GetByTestId("wizard-documents").Locator("tbody tr")).ToHaveCountAsync(1);
        await page.GetByTestId("wizard-doctype").SelectOptionAsync("EventPlan");
        await page.GetByTestId("wizard-file").SetInputFilesAsync(new FilePayload { Name = "plan.pdf", MimeType = "application/pdf", Buffer = Files.Pdf() });
        await page.GetByTestId("wizard-upload").ClickAsync();
        await Assertions.Expect(page.GetByTestId("wizard-documents").Locator("tbody tr")).ToHaveCountAsync(2);
        await Assertions.Expect(page.GetByTestId("wizard-ready")).ToBeVisibleAsync();
        await BrowserFixture.AssertAccessibleAsync(page, "wizard step 4");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);

        await page.GetByTestId("wizard-submit").ClickAsync();
        await page.WaitForURLAsync($"**/applications/{id}?submitted=1");
        await Assertions.Expect(page.GetByTestId("detail-status")).ToContainTextAsync("Submitted");
        Assert.Equal(EventPermits.Domain.CaseStatus.Submitted, await _s.StatusAsync(Guid.Parse(id)));

        // Resuming a submitted application shows it is locked instead of an editable form.
        await OpenAsync(page, $"/applications/new/{id}/step/1");
        await Assertions.Expect(page.GetByTestId("wizard-locked")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Wizard_resumes_a_saved_draft_and_is_blocked_for_other_roles()
    {
        var draft = await _s.DraftAsync("intake.alpha");
        var page = await app.PageAsync("intake.alpha");
        await OpenAsync(page, "/applications/new");
        await Assertions.Expect(page.GetByTestId("wizard-drafts")).ToContainTextAsync(draft.Reference);
        await OpenAsync(page, $"/applications/new/{draft.Id}/step/2");
        await Assertions.Expect(page.GetByTestId("wizard-title")).ToHaveValueAsync("Remembrance service for a fictional veteran");
        await Assertions.Expect(page.GetByTestId("wizard-step-1")).ToHaveClassAsync(new Regex("is-done"));

        // Another coordinator cannot open it (out of scope: 404 without details).
        var other = await app.PageAsync("intake.bravo");
        await OpenAsync(other, $"/applications/new/{draft.Id}/step/1");
        await Assertions.Expect(other.GetByTestId("problem")).ToHaveAttributeAsync("data-status", "404");

        var reviewer = await app.PageAsync("reviewer.charlie");
        await OpenAsync(reviewer, "/applications/new");
        await Assertions.Expect(reviewer.GetByTestId("forbidden")).ToBeVisibleAsync();
    }

    // ------------------------------------------------------------------ 04 Application Detail

    [Fact]
    public async Task Detail_shows_the_case_explains_blocked_actions_and_records_comments()
    {
        var c = await _s.SubmittedAsync();
        var page = await app.PageAsync("reviewer.charlie");
        await OpenAsync(page, $"/applications/{c.Id}");
        await Assertions.Expect(page.Locator("h1")).ToHaveTextAsync(c.Reference);
        await Assertions.Expect(page.GetByTestId("detail-status")).ToContainTextAsync("Submitted");
        await Assertions.Expect(page.GetByTestId("detail-summary")).ToContainTextAsync("Avery Practice-Alder");
        await Assertions.Expect(page.GetByTestId("action-T4")).ToContainTextAsync("Blocked: Verify the applicant against the registry first.");
        await Assertions.Expect(page.GetByTestId("detail-documents").GetByTestId("detail-document")).ToHaveCountAsync(2);
        await Assertions.Expect(page.GetByTestId("timeline-item").First).ToBeVisibleAsync();
        await BrowserFixture.AssertAccessibleAsync(page, "application detail");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "04-application-detail");

        await page.GetByTestId("comment-add").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-body")).ToBeVisibleAsync();
        await page.GetByTestId("comment-body").FillAsync("Registry check scheduled for this afternoon.");
        await page.GetByTestId("comment-add").ClickAsync();
        await Assertions.Expect(page.GetByTestId("comment")).ToContainTextAsync("Registry check scheduled for this afternoon.");
        await Assertions.Expect(page.Locator("[data-testid=timeline-item][data-action='Comment.Added']")).ToContainTextAsync("Registry check scheduled");

        // A signed download link with a visible expiry.
        await page.GetByTestId("doc-link-issue").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("doc-expiry").First).ToHaveTextAsync(new Regex(@"expires in \d+:\d\d"));
        Assert.StartsWith($"/files/", await page.GetByTestId("doc-download").First.GetAttributeAsync("href"));
    }

    [Fact]
    public async Task Detail_runs_a_permitted_transition_with_validation_and_a_conflict_panel()
    {
        var c = await _s.SubmittedAsync();
        var page = await app.PageAsync("manager.echo");
        await OpenAsync(page, $"/applications/{c.Id}");

        await page.GetByTestId("action-open-T14").ClickAsync();
        await page.GetByTestId("action-text").FillAsync("too short");
        await page.GetByTestId("action-confirm").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-reason")).ToContainTextAsync("10 to 1000 characters");
        Assert.Equal("true", await page.GetByTestId("action-text").GetAttributeAsync("aria-invalid"));

        // Someone else changes the case meanwhile: the write is refused with a reload/merge panel, nothing is lost.
        using (var reviewer = await app.SignInAsync("reviewer.charlie"))
            (await reviewer.PostAsync($"/api/applications/{c.Id}/comments", new { version = await _s.VersionAsync(c.Id), body = "Changed in another session." })).Ok();
        await page.GetByTestId("action-text").FillAsync("Venue closed for emergency maintenance.");
        await page.GetByTestId("action-confirm").ClickAsync();
        await Assertions.Expect(page.GetByTestId("conflict")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("problem")).ToHaveAttributeAsync("data-status", "409");
        await BrowserFixture.AssertAccessibleAsync(page, "application detail conflict");

        await page.GetByTestId("conflict-reapply").ClickAsync();
        await Assertions.Expect(page.GetByTestId("detail-status")).ToContainTextAsync("Cancelled");
        Assert.Equal(EventPermits.Domain.CaseStatus.Cancelled, await _s.StatusAsync(c.Id));
        await Assertions.Expect(page.GetByTestId("detail-no-actions")).ToContainTextAsync("Cancelled");
        await Assertions.Expect(page.Locator("[data-testid=timeline-item][data-action='Transition.Cancel']")).ToContainTextAsync("T14");
    }

    [Fact]
    public async Task Detail_finder_opens_by_reference_and_scope_is_enforced()
    {
        var c = await _s.SubmittedAsync();
        var page = await app.PageAsync("scheduler.delta");
        await OpenAsync(page, "/applications/detail");
        await BrowserFixture.AssertAccessibleAsync(page, "application finder");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await page.GetByTestId("finder-reference").FillAsync("APP-1999-99999");
        await page.GetByTestId("finder-open").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-reference")).ToContainTextAsync("No case APP-1999-99999");
        await page.GetByTestId("finder-reference").FillAsync(c.Reference.ToLowerInvariant());
        await page.GetByTestId("finder-open").ClickAsync();
        await page.WaitForURLAsync($"**/applications/{c.Id}");
        await Assertions.Expect(page.Locator("h1")).ToHaveTextAsync(c.Reference);

        var other = await app.PageAsync("intake.bravo");
        await OpenAsync(other, $"/applications/{c.Id}");
        await Assertions.Expect(other.GetByTestId("problem")).ToHaveAttributeAsync("data-status", "404");
        await Assertions.Expect(other.GetByTestId("detail-summary")).ToHaveCountAsync(0);

        var admin = await app.PageAsync("admin.india");
        await OpenAsync(admin, "/applications/detail");
        await Assertions.Expect(admin.GetByTestId("forbidden")).ToBeVisibleAsync();
    }

    // ------------------------------------------------------------------ 23 Document Vault

    [Fact]
    public async Task Vault_filters_previews_with_expiring_links_and_shows_file_history()
    {
        var c = await _s.DraftAsync();
        using (var intake = await app.SignInAsync("intake.alpha"))
            (await intake.UploadAsync($"/api/applications/{c.Id}/documents", "site-photo.png", "image/png", Files.Png(),
                new Dictionary<string, string> { ["documentType"] = "SitePlan", ["version"] = (await _s.VersionAsync(c.Id)).ToString() })).Ok();

        var page = await app.PageAsync("reviewer.charlie");
        await OpenAsync(page, $"/documents?case={c.Reference}");
        await Assertions.Expect(page.GetByTestId("vault-row")).ToHaveCountAsync(3);
        await BrowserFixture.AssertAccessibleAsync(page, "document vault");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "23-document-vault");

        var photo = page.Locator("[data-testid=vault-row][data-file='site-photo.png']");
        await photo.GetByTestId("doc-link-issue").ClickAsync();
        await Assertions.Expect(photo.GetByTestId("doc-expiry")).ToHaveTextAsync(new Regex(@"expires in \d+:\d\d"));

        await photo.GetByTestId("vault-preview").ClickAsync();
        var image = page.GetByTestId("vault-preview-image");
        await Assertions.Expect(image).ToBeAttachedAsync();
        var src = await image.GetAttributeAsync("src");
        Assert.Contains("/preview?exp=", src);
        Assert.Equal(200, await page.EvaluateAsync<int>("async s => (await fetch(s)).status", src));
        await Assertions.Expect(page.Locator("[data-testid=vault-history-list] li[data-action='Attachment.LinkIssued']").First).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("[data-testid=vault-history-list] li[data-action='Attachment.Uploaded']")).ToHaveCountAsync(1);
        await BrowserFixture.AssertAccessibleAsync(page, "document vault preview");

        // Tampering with the signature never yields the file; it lands on the friendly invalid-link page.
        await page.GotoAsync(src!.Replace("sig=", "sig=x"));
        await page.Locator("[data-interactive]").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        await Assertions.Expect(page.GetByTestId("link-expired")).ToBeVisibleAsync();
        await page.GetByTestId("link-regenerate").ClickAsync();
        await Assertions.Expect(page.GetByTestId("link-download")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Vault_validates_filters_and_uploads_to_an_editable_case()
    {
        var c = await _s.DraftAsync("intake.alpha");
        var page = await app.PageAsync("intake.alpha");
        await OpenAsync(page, "/documents?from=2026-12-01&to=2026-11-01");
        await Assertions.Expect(page.GetByTestId("error-to")).ToContainTextAsync("on or after the start date");

        await OpenAsync(page, $"/documents?case={c.Reference}");
        await Assertions.Expect(page.GetByTestId("vault-row")).ToHaveCountAsync(2);
        await page.GetByTestId("vault-upload-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-uploadCase")).ToBeVisibleAsync();

        await page.GetByTestId("vault-upload-case").SelectOptionAsync(c.Id.ToString());
        await page.GetByTestId("vault-upload-type").SelectOptionAsync("InsuranceCertificate");
        await page.GetByTestId("vault-upload-file").SetInputFilesAsync(new FilePayload { Name = "notes.txt", MimeType = "text/plain", Buffer = "plain text"u8.ToArray() });
        await page.GetByTestId("vault-upload-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("error-file")).ToContainTextAsync("Only PDF, PNG, JPG files are accepted.");

        await page.GetByTestId("vault-upload-file").SetInputFilesAsync(new FilePayload { Name = "insurance.pdf", MimeType = "application/pdf", Buffer = Files.Pdf() });
        await page.GetByTestId("vault-upload-submit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("vault-row")).ToHaveCountAsync(3);
        await Assertions.Expect(page.Locator("[data-testid=vault-row][data-file='insurance.pdf']")).ToContainTextAsync("Insurance certificate");
    }

    [Fact]
    public async Task Vault_gives_auditors_metadata_only_and_blocks_attendants()
    {
        var c = await _s.SubmittedAsync();
        var auditor = await app.PageAsync("auditor.hotel");
        await OpenAsync(auditor, $"/documents?case={c.Reference}");
        await Assertions.Expect(auditor.GetByTestId("vault-metadata-only")).ToBeVisibleAsync();
        await Assertions.Expect(auditor.GetByTestId("vault-row")).ToHaveCountAsync(2);
        await Assertions.Expect(auditor.GetByTestId("doc-metadata-only")).ToHaveCountAsync(2);
        await Assertions.Expect(auditor.GetByTestId("vault-preview")).ToHaveCountAsync(0);
        await Assertions.Expect(auditor.GetByTestId("doc-link-issue")).ToHaveCountAsync(0);
        await BrowserFixture.AssertAccessibleAsync(auditor, "document vault (auditor)");

        var attendant = await app.PageAsync("attendant.foxtrot");
        await OpenAsync(attendant, "/documents");
        await Assertions.Expect(attendant.GetByTestId("forbidden")).ToBeVisibleAsync();
    }
}
