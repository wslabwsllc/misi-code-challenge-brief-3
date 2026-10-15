using System.Net;
using EventPermits.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace EventPermits.BrowserTests;

public sealed class SchedulingPagesFixture(TestInfrastructure infra) : BrowserFixture(infra);

/// <summary>
/// Scheduling and venue pages (07 Event Calendar, 08 Venue Availability, 09 Resource Planner, 17 Administration: Venues,
/// 21 Regional Calendar Sync, 26 Venue Map): real data, a main action end to end, a validation error, role blocking,
/// axe and phone width (E6-01, E6-03, E6-04, E6-06, E6-07, E6-08).
/// </summary>
public sealed class SchedulingPagesTests(SchedulingPagesFixture app) : IClassFixture<SchedulingPagesFixture>
{
    private readonly Scenario _scenario = new(app);

    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd");

    private Task<Guid> CemeteryIdAsync(string code) => app.QueryAsync(db => db.Cemeteries.Where(c => c.Code == code).Select(c => c.Id).FirstAsync());

    private Task<Guid> ResourceIdAsync(string cemetery, string code) =>
        app.QueryAsync(db => db.Resources.Where(r => r.Cemetery!.Code == cemetery && r.Code == code).Select(r => r.Id).FirstAsync());

    private static string Local(DateOnly date, TimeOnly time, string zone = "America/New_York") =>
        new DateTimeOffset(date.ToDateTime(time), TimeZoneInfo.FindSystemTimeZoneById(zone).GetUtcOffset(date.ToDateTime(time))).ToUniversalTime().ToString("O");

    private async Task AddClosureAsync(Guid venueId, DateOnly date, TimeOnly from, TimeOnly to, string reason)
    {
        using var admin = await app.SignInAsync("admin.india");
        (await admin.PostAsync("/api/admin/blackouts", new { venueId, startUtc = Local(date, from), endUtc = Local(date, to), reason })).Ok();
    }

    // ------------------------------------------------------------------ 07 Event Calendar

    [Fact]
    public async Task Event_calendar_shows_entries_in_month_week_and_day_views_and_is_keyboard_operable()
    {
        var c = await _scenario.ReservedAsync();
        await AddClosureAsync(c.VenueId, c.Date, new TimeOnly(16, 0), new TimeOnly(17, 30), "Path resurfacing (calendar test)");

        var page = await app.PageAsync("scheduler.delta");
        await BrowserFixture.GoAsync(page, $"/calendar?date={D(c.Date)}");
        var legend = page.Locator(".cal-legend");
        foreach (var kind in new[] { "Reservation", "Regional calendar event", "Partner event", "Closure" })
            await Assertions.Expect(legend).ToContainTextAsync(kind);
        var reservation = page.GetByTestId("cal-item").Filter(new() { HasText = c.Reference });
        await Assertions.Expect(reservation.First).ToBeVisibleAsync();
        await Assertions.Expect(reservation.First).ToContainTextAsync("Tentatively Reserved");
        await Assertions.Expect(page.GetByTestId("cal-item").Filter(new() { HasText = "Path resurfacing" }).First).ToBeVisibleAsync();
        await BrowserFixture.AssertAccessibleAsync(page, "calendar month");

        // Arrow keys move between days (roving tabindex).
        var day = page.Locator($"td[data-date='{D(c.Date)}'] [data-cal-day]");
        await day.FocusAsync();
        await page.Keyboard.PressAsync("ArrowRight");
        Assert.Equal(D(c.Date.AddDays(1)), await page.EvaluateAsync<string>("() => document.activeElement.closest('td').dataset.date"));
        await page.Keyboard.PressAsync("ArrowLeft");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**view=day**");
        await Assertions.Expect(page.GetByTestId("cal-dayview")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("cal-entry").Filter(new() { HasText = c.Reference })).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator("[data-testid=cal-entry][data-kind=blackout]")).ToHaveCountAsync(1);

        // Week view keeps the filters in the URL.
        await page.GetByTestId("cal-view-week").ClickAsync();
        await page.WaitForURLAsync("**view=week**");
        await Assertions.Expect(page.GetByTestId("cal-week").GetByTestId("cal-item").Filter(new() { HasText = c.Reference })).ToHaveCountAsync(1);
        await page.GetByTestId("cal-kind").SelectOptionAsync("blackout");
        await page.WaitForURLAsync("**kind=blackout**");
        await Assertions.Expect(page.GetByTestId("cal-week").GetByTestId("cal-item").Filter(new() { HasText = c.Reference })).ToHaveCountAsync(0);

        // Validation on the go-to-date control.
        await page.GetByTestId("cal-jump-go").ClickAsync();
        await Assertions.Expect(page.GetByTestId("field-error")).ToContainTextAsync("Enter a date");
        Assert.Equal("true", await page.GetByTestId("cal-jump-date").GetAttributeAsync("aria-invalid"));

        // Day view of one venue lists free starts; a free start opens the reservation proposal prefilled.
        await BrowserFixture.GoAsync(page, $"/calendar?view=day&date={D(c.Date)}&venue={c.VenueId}");
        await Assertions.Expect(page.GetByTestId("cal-busy-slot").First).ToBeVisibleAsync();
        var free = page.GetByTestId("cal-free-slot").First;
        await free.ClickAsync();
        await page.WaitForURLAsync("**/venues/availability?**");
        await Assertions.Expect(page.GetByTestId("proposal-start")).Not.ToHaveValueAsync("");
        Assert.Contains($"venue={c.VenueId}", page.Url);

        await BrowserFixture.GoAsync(page, $"/calendar?date={D(c.Date)}");
        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "07-calendar");
    }

    // ------------------------------------------------------------------ 08 Venue Availability

    [Fact]
    public async Task Venue_availability_checks_collisions_live_reserves_T6_and_submits_T7()
    {
        var c = await _scenario.EligibleAsync();
        await AddClosureAsync(c.VenueId, c.Date, new TimeOnly(14, 0), new TimeOnly(16, 0), "Chapel roof repair (availability test)");

        var page = await app.PageAsync("scheduler.delta");
        await BrowserFixture.GoAsync(page, $"/venues/availability?venue={c.VenueId}&date={D(c.Date)}");
        await Assertions.Expect(page.Locator("[data-testid=timeline-item][data-kind=blackout]")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("desk-row").Filter(new() { HasText = c.Reference })).ToHaveCountAsync(1);

        await page.GetByTestId("proposal-case").SelectOptionAsync(c.Id.ToString());
        await Assertions.Expect(page.GetByTestId("proposal-attendees")).ToHaveValueAsync("40");

        // A start inside the closure (including the setup buffer) is refused with every reason and free alternatives.
        await page.GetByTestId("proposal-start").FillAsync("14:30");
        await Assertions.Expect(page.Locator("[data-testid=check-conflict][data-kind=Blackout]")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("suggestion").First).ToBeVisibleAsync();
        await BrowserFixture.AssertAccessibleAsync(page, "venue availability with conflicts");

        await page.GetByTestId("proposal-start").FillAsync(c.Start.ToString("HH:mm"));
        await Assertions.Expect(page.GetByTestId("check-free")).ToBeVisibleAsync();

        // Client and server validation render as field errors.
        await page.GetByTestId("proposal-attendees").FillAsync("");
        await page.GetByTestId("reserve").ClickAsync();
        await Assertions.Expect(page.GetByTestId("problem")).ToHaveAttributeAsync("data-status", "400");
        Assert.Equal("true", await page.GetByTestId("proposal-attendees").GetAttributeAsync("aria-invalid"));
        await Assertions.Expect(page.Locator("#err-attendeeCount")).ToContainTextAsync("At least one attendee");

        await page.GetByTestId("proposal-attendees").FillAsync("40");
        await page.GetByTestId("proposal-needs").FillAsync("One attendant at the chapel door.");
        await page.GetByTestId("reserve").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("tentatively reserved");
        var row = page.GetByTestId("desk-row").Filter(new() { HasText = c.Reference });
        await Assertions.Expect(row).ToContainTextAsync("Tentatively Reserved");
        await Assertions.Expect(page.Locator("[data-testid=timeline-item][data-kind=reservation]")).ToHaveCountAsync(1);

        await row.GetByTestId("submit-approval").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("submitted for approval");
        await Assertions.Expect(row).ToContainTextAsync("Pending Approval");
        Assert.Equal(EventPermits.Domain.CaseStatus.PendingApproval, await _scenario.StatusAsync(c.Id));

        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "08-venue-availability");

        // Operations Managers can check slots but have no reservation controls.
        var manager = await app.PageAsync("manager.echo");
        await BrowserFixture.GoAsync(manager, $"/venues/availability?venue={c.VenueId}&date={D(c.Date)}");
        await Assertions.Expect(manager.GetByTestId("read-only")).ToBeVisibleAsync();
        Assert.Equal(0, await manager.GetByTestId("reserve").CountAsync());
    }

    // ------------------------------------------------------------------ 09 Resource Planner

    [Fact]
    public async Task Resource_planner_allocates_within_inventory_and_rejects_over_allocation()
    {
        var c = await _scenario.ReservedAsync();
        var cemetery = await CemeteryIdAsync("RMP");
        var chairs = await ResourceIdAsync("RMP", "CHAIRS");

        var page = await app.PageAsync("scheduler.delta");
        await BrowserFixture.GoAsync(page, $"/resources?cemetery={cemetery}&date={D(c.Date)}");
        await Assertions.Expect(page.GetByTestId("res-line")).ToHaveCountAsync(8);
        var booking = page.GetByTestId("res-booking").Filter(new() { HasText = c.Reference });
        await Assertions.Expect(booking).ToHaveCountAsync(1);
        await BrowserFixture.AssertAccessibleAsync(page, "resource planner");

        await booking.GetByTestId("res-alloc-save").ClickAsync();
        await Assertions.Expect(booking.GetByTestId("field-error").First).ToContainTextAsync("Choose the stock");
        Assert.Equal("true", await booking.GetByTestId("res-alloc-resource").GetAttributeAsync("aria-invalid"));

        await booking.GetByTestId("res-alloc-resource").SelectOptionAsync(chairs.ToString());
        await booking.GetByTestId("res-alloc-qty").FillAsync("25");
        await booking.GetByTestId("res-alloc-save").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("25 allocated");
        await Assertions.Expect(page.Locator("[data-testid=res-line][data-code=CHAIRS] [data-testid=res-allocated]")).ToHaveTextAsync("25");
        await Assertions.Expect(page.Locator("[data-testid=res-line][data-code=CHAIRS] [data-testid=res-remaining]")).ToHaveTextAsync("375");
        await Assertions.Expect(booking.GetByTestId("res-allocation")).ToContainTextAsync("25 × Folding chairs");

        // The server refuses more than the stock left for the window (422 with the reason).
        await booking.GetByTestId("res-alloc-resource").SelectOptionAsync(chairs.ToString());
        await booking.GetByTestId("res-alloc-qty").FillAsync("401");
        await booking.GetByTestId("res-alloc-save").ClickAsync();
        await Assertions.Expect(page.GetByTestId("problem")).ToHaveAttributeAsync("data-status", "422");
        await Assertions.Expect(page.GetByTestId("problem")).ToContainTextAsync("only 400 of 400 available");

        await booking.GetByTestId("res-release").ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid=res-line][data-code=CHAIRS] [data-testid=res-allocated]")).ToHaveTextAsync("0");

        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "09-resource-planner");
    }

    [Fact]
    public async Task Resource_planner_lets_operations_managers_assign_attendants_after_approval()
    {
        var c = await _scenario.ApprovedAsync();
        var cemetery = await CemeteryIdAsync("RMP");
        var page = await app.PageAsync("manager.echo");
        await BrowserFixture.GoAsync(page, $"/resources?cemetery={cemetery}&date={D(c.Date)}");
        var booking = page.GetByTestId("res-booking").Filter(new() { HasText = c.Reference });
        Assert.Equal(0, await booking.GetByTestId("res-alloc-save").CountAsync()); // allocation belongs to Scheduling
        await booking.GetByTestId("res-assign").ClickAsync();
        await Assertions.Expect(booking.GetByTestId("field-error")).ToContainTextAsync("Choose an attendant");
        await booking.GetByTestId("res-attendant-select").SelectOptionAsync(app.UserIds["attendant.golf"]);
        await booking.GetByTestId("res-assign").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("assigned to");
        await Assertions.Expect(booking.GetByTestId("res-attendant")).ToContainTextAsync("Practice Attendant Golf");
        await BrowserFixture.AssertAccessibleAsync(page, "resource planner (manager)");
    }

    // ------------------------------------------------------------------ 17 Administration: Venues

    [Fact]
    public async Task Admin_venues_edits_venues_closures_stock_and_categories_without_deleting()
    {
        var page = await app.PageAsync("admin.india");
        await BrowserFixture.GoAsync(page, "/admin/venues");
        await Assertions.Expect(page.GetByTestId("venue-row")).ToHaveCountAsync(12);
        await BrowserFixture.AssertAccessibleAsync(page, "admin venues");

        // Create a venue, then deactivate it (no hard delete).
        await page.GetByTestId("venue-new").ClickAsync();
        await page.GetByTestId("venue-save").ClickAsync();
        await Assertions.Expect(page.Locator("#err-name")).ToContainTextAsync("Name is required");
        Assert.Equal("true", await page.GetByTestId("venue-name").GetAttributeAsync("aria-invalid"));
        await page.GetByTestId("venue-code").FillAsync("RMP-ANNEX");
        await page.GetByTestId("venue-name").FillAsync("Riverside Annex Room");
        await page.GetByTestId("venue-address").FillAsync("30 Annex Lane, Fairhaven, Practice State");
        await page.GetByTestId("venue-capacity").FillAsync("9000");
        await page.GetByTestId("venue-save").ClickAsync();
        await Assertions.Expect(page.Locator("#err-capacity")).ToContainTextAsync("Capacity must be 1 to 5000"); // server-side rule
        await page.GetByTestId("venue-capacity").FillAsync("25");
        await page.GetByTestId("venue-save").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("RMP-ANNEX saved");
        var annex = page.Locator("[data-testid=venue-row][data-code=RMP-ANNEX]");
        await Assertions.Expect(annex).ToContainTextAsync("Active");
        await annex.GetByTestId("venue-toggle").ClickAsync();
        await Assertions.Expect(annex).ToContainTextAsync("Inactive");
        await Assertions.Expect(page.GetByTestId("venue-row")).ToHaveCountAsync(13);

        // Closures: validation, then a closure with an overlap warning list.
        await page.GetByTestId("tab-closures").ClickAsync();
        await page.WaitForURLAsync("**tab=closures**");
        await page.GetByTestId("closure-venue").SelectOptionAsync(new SelectOptionValue { Label = "Mesa Vista Pavilion (Mesa Vista Remembrance Gardens)" });
        await page.GetByTestId("closure-start").FillAsync($"{D(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(200))}T08:00");
        await page.GetByTestId("closure-end").FillAsync($"{D(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(200))}T12:00");
        await page.GetByTestId("closure-add").ClickAsync();
        await Assertions.Expect(page.Locator("#err-reason")).ToContainTextAsync("Reason is required");
        await page.GetByTestId("closure-reason").FillAsync("Irrigation works (admin test)");
        await page.GetByTestId("closure-add").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("Closure added");
        var closure = page.GetByTestId("closure-row").Filter(new() { HasText = "Irrigation works" });
        await Assertions.Expect(closure).ToContainTextAsync("Closed");
        await closure.GetByTestId("closure-toggle").ClickAsync();
        await Assertions.Expect(closure).ToContainTextAsync("Lifted");
        await BrowserFixture.AssertAccessibleAsync(page, "admin venues closures");

        // Inventory and categories.
        await page.GetByTestId("tab-inventory").ClickAsync();
        await page.Locator("[data-testid=resource-row][data-code=PODIUM]").First.GetByTestId("resource-edit").ClickAsync();
        await page.GetByTestId("resource-total").FillAsync("5");
        await page.GetByTestId("resource-save").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("Podiums saved");

        await page.GetByTestId("tab-categories").ClickAsync();
        await page.GetByTestId("category-new").ClickAsync();
        await page.GetByTestId("category-code").FillAsync("VIGIL");
        await page.GetByTestId("category-name").FillAsync("Candlelight vigil");
        await page.GetByTestId("category-save").ClickAsync();
        await Assertions.Expect(page.Locator("#err-checklist")).ToContainTextAsync("at least one");
        await page.GetByTestId("category-checklist").FillAsync("Fire safety plan reviewed\nQuiet hours respected");
        await page.GetByTestId("category-save").ClickAsync();
        await Assertions.Expect(page.Locator("[data-testid=category-row][data-code=VIGIL]")).ToContainTextAsync("2 items");

        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "17-admin-venues");

        // Scheduling Officers read the same data without edit controls.
        var scheduler = await app.PageAsync("scheduler.delta");
        await BrowserFixture.GoAsync(scheduler, "/admin/venues");
        await Assertions.Expect(scheduler.GetByTestId("read-only")).ToBeVisibleAsync();
        Assert.Equal(0, await scheduler.GetByTestId("venue-edit").CountAsync());
        Assert.Equal(0, await scheduler.GetByTestId("venue-new").CountAsync());
    }

    // ------------------------------------------------------------------ 21 Regional Calendar Sync

    [Fact]
    public async Task Calendar_sync_imports_events_shows_offline_state_and_validates_reconciliation()
    {
        var calendarMock = app.MockAdmin("calendar");
        await calendarMock.SetModeAsync("conflict");
        var c = await _scenario.ReservedAsync();
        await _scenario.ReconcileCalendarAsync(); // pushes the hold: the calendar answers 409, raising a reconciliation item
        await calendarMock.SetModeAsync("normal");

        var page = await app.PageAsync("scheduler.delta");
        await BrowserFixture.GoAsync(page, "/integrations/calendar");
        var item = page.GetByTestId("queue-row").Filter(new() { HasText = c.Reference });
        await Assertions.Expect(item).ToHaveAttributeAsync("data-status", "Open");
        await BrowserFixture.AssertAccessibleAsync(page, "calendar sync");

        // Resolution needs a reason.
        await item.GetByTestId("queue-resolve").ClickAsync();
        await item.GetByTestId("queue-resolve-submit").ClickAsync();
        await Assertions.Expect(item.GetByTestId("field-error")).ToContainTextAsync("Resolution reason is required");
        Assert.Equal("true", await item.GetByTestId("queue-reason").GetAttributeAsync("aria-invalid"));

        // Offline: the sync fails with a retryable 503-style problem; Retry succeeds once the calendar is back.
        await calendarMock.SetModeAsync("offline");
        await page.GetByTestId("sync-now").ClickAsync();
        await Assertions.Expect(page.GetByTestId("problem")).ToHaveAttributeAsync("data-status", "503");
        await Assertions.Expect(page.GetByTestId("sync-offline")).ToBeVisibleAsync();
        await calendarMock.SetModeAsync("normal");
        await page.GetByTestId("retry").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("Sync complete");
        await Assertions.Expect(page.GetByTestId("sync-connection")).ToContainTextAsync("Connected");
        await Assertions.Expect(page.GetByTestId("event-row").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("run-row").First).ToHaveAttributeAsync("data-ok", "True");

        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "21-calendar-sync");
    }

    // ------------------------------------------------------------------ 26 Venue Map

    [Fact]
    public async Task Venue_map_requires_an_explicit_choice_on_a_tie_and_plots_confirmed_coordinates()
    {
        var garden = await _scenario.VenueIdAsync("CHG-GARDEN");
        var page = await app.PageAsync("admin.india");
        await BrowserFixture.GoAsync(page, $"/venues/map?venue={garden}");
        await Assertions.Expect(page.GetByTestId("map-row")).ToHaveCountAsync(4); // the venues of the selected cemetery
        await Assertions.Expect(page.Locator("[data-testid=map-row][data-code=CHG-GARDEN] [data-testid=map-row-check]")).ToContainTextAsync("Missing");
        await BrowserFixture.AssertAccessibleAsync(page, "venue map");

        await page.GetByTestId("geo-lookup").ClickAsync();
        await Assertions.Expect(page.GetByTestId("geo-candidate")).ToHaveCountAsync(2);
        Assert.Equal(0, await page.Locator("[data-testid=geo-candidate]:checked").CountAsync()); // tie: nothing preselected
        await page.GetByTestId("geo-confirm").ClickAsync();
        await Assertions.Expect(page.Locator("#err-candidateId")).ToContainTextAsync("Choose one of the candidates");
        await BrowserFixture.AssertAccessibleAsync(page, "venue map candidates");

        await page.GetByTestId("geo-candidate").First.CheckAsync();
        await page.GetByTestId("geo-confirm").ClickAsync();
        await Assertions.Expect(page.GetByTestId("success")).ToContainTextAsync("confirmed");
        await Assertions.Expect(page.Locator("[data-testid=map-row][data-code=CHG-GARDEN] [data-testid=map-row-check]")).ToHaveTextAsync("Verified");
        await Assertions.Expect(page.Locator("[data-testid=map-marker][data-code=CHG-GARDEN]")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("geo-history")).ToContainTextAsync("Confirmed");

        // Geocoder offline: no invented coordinates, Retry offered.
        var geocode = app.MockAdmin("geocode");
        await geocode.SetModeAsync("offline");
        await page.GetByTestId("geo-lookup").ClickAsync();
        await Assertions.Expect(page.GetByTestId("geo-unavailable")).ToBeVisibleAsync();
        await geocode.SetModeAsync("normal");
        await page.GetByTestId("geo-retry").ClickAsync();
        await Assertions.Expect(page.GetByTestId("geo-candidate")).ToHaveCountAsync(2);

        // Selecting a table row highlights its marker.
        var lawn = page.Locator("[data-testid=map-row][data-code=CHG-LAWN]");
        await lawn.GetByTestId("map-select").ClickAsync();
        await Assertions.Expect(lawn).ToHaveAttributeAsync("aria-current", "true");

        await BrowserFixture.AssertNoHorizontalScrollAsync(page);
        await app.ScreenshotAsync(page, "26-venue-map");

        var manager = await app.PageAsync("manager.echo");
        await BrowserFixture.GoAsync(manager, $"/venues/map?venue={garden}");
        await Assertions.Expect(manager.GetByTestId("read-only")).ToBeVisibleAsync();
        Assert.Equal(0, await manager.GetByTestId("geo-lookup").CountAsync());
    }

    // ------------------------------------------------------------------ access

    [Theory]
    [InlineData("/calendar", "intake.alpha")]
    [InlineData("/venues/availability", "attendant.foxtrot")]
    [InlineData("/resources", "auditor.hotel")]
    [InlineData("/admin/venues", "manager.echo")]
    [InlineData("/integrations/calendar", "reviewer.charlie")]
    [InlineData("/venues/map", "intake.alpha")]
    public async Task Pages_are_blocked_for_roles_without_the_capability(string route, string user)
    {
        var page = await app.PageAsync(user);
        await BrowserFixture.GoAsync(page, route);
        await Assertions.Expect(page.GetByTestId("forbidden")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Crafted_requests_for_page_actions_are_refused_without_the_capability()
    {
        using var scheduler = await app.SignInAsync("scheduler.delta");
        (await scheduler.PostAsync("/api/admin/categories", new { code = "HACK", name = "Not allowed", reviewChecklist = new[] { "x" }, isActive = true }))
            .Expect(HttpStatusCode.Forbidden);
        using var intake = await app.SignInAsync("intake.alpha");
        (await intake.GetAsync("/api/schedule/desk")).Expect(HttpStatusCode.Forbidden);
        (await intake.GetAsync($"/api/resources/planner?cemeteryId={await CemeteryIdAsync("RMP")}&date={D(DateOnly.FromDateTime(DateTime.UtcNow))}")).Expect(HttpStatusCode.Forbidden);
        var admin = await app.SignInAsync("admin.india");
        (await admin.PostAsync("/api/admin/categories", new { code = "API", name = "API category", reviewChecklist = new[] { "Checked" }, isActive = true })).Ok();
        (await admin.GetAsync("/api/calendar/overview")).Ok();
        admin.Dispose();
    }
}
