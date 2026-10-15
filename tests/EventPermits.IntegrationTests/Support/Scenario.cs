using System.Text;
using System.Text.Json.Nodes;
using EventPermits.Application;
using EventPermits.Application.Services;
using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EventPermits.IntegrationTests.Support;

/// <summary>Valid synthetic files for upload tests (real magic bytes).</summary>
public static class Files
{
    public static byte[] Pdf(int size = 600)
    {
        var head = Encoding.ASCII.GetBytes("%PDF-1.4\n% fictional practice document\n");
        var bytes = new byte[Math.Max(size, head.Length)];
        head.CopyTo(bytes, 0);
        return bytes;
    }

    public static byte[] Png(int size = 200)
    {
        var bytes = new byte[Math.Max(size, 16)];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    public static byte[] Jpeg(int size = 200)
    {
        var bytes = new byte[Math.Max(size, 16)];
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.CopyTo(bytes, 0);
        return bytes;
    }
}

/// <summary>Drives cases through the workflow over the real HTTP API, as the UI would.</summary>
public sealed class Scenario(AppFixture app)
{
    private static int _slot;

    public sealed record Case(Guid Id, string Reference, DateOnly Date, TimeOnly Start, Guid VenueId, string VenueCode);

    /// <summary>A free future weekday slot well beyond the mock calendar's events (which span -3..+17 days).</summary>
    public static (DateOnly Date, TimeOnly Start) NextSlot()
    {
        var n = Interlocked.Increment(ref _slot);
        var date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30 + n);
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1);
        return (date, new TimeOnly(10 + n % 5, 0));
    }

    public async Task<Guid> VenueIdAsync(string code) => await app.QueryAsync(db => db.Venues.Where(v => v.Code == code).Select(v => v.Id).FirstAsync());
    public async Task<Guid> CategoryIdAsync(string code) => await app.QueryAsync(db => db.EventCategories.Where(v => v.Code == code).Select(v => v.Id).FirstAsync());

    public async Task<uint> VersionAsync(Guid id) => await app.QueryAsync(db => db.Applications.Where(a => a.Id == id).Select(a => a.Version).FirstAsync());
    public async Task<CaseStatus> StatusAsync(Guid id) => await app.QueryAsync(db => db.Applications.Where(a => a.Id == id).Select(a => a.Status).FirstAsync());

    /// <summary>A complete draft (all steps, required documents) owned by <paramref name="intakeUser"/>.</summary>
    public async Task<Case> DraftAsync(string intakeUser = "intake.alpha", string registryRef = "REF-10001", string venueCode = "RMP-CHAPEL", int attendees = 40, (DateOnly, TimeOnly)? slot = null)
    {
        using var intake = await app.SignInAsync(intakeUser);
        var created = (await intake.SendAsync(HttpMethod.Post, "/api/applications", new { }, headers: new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString() })).Ok();
        var id = created.Id;
        var (date, start) = slot ?? NextSlot();
        var venueId = await VenueIdAsync(venueCode);
        var version = (await intake.PutAsync($"/api/applications/{id}/applicant", new
        {
            version = created.Version, complete = true, fullName = "Avery Practice-Alder", organization = "Fictional Veterans Fellowship Post 12",
            email = "avery.alder@example.test", phone = "+1-555-0101", registryReference = registryRef,
        })).Ok().Version;
        version = (await intake.PutAsync($"/api/applications/{id}/event", new
        {
            version, complete = true, title = "Remembrance service for a fictional veteran", categoryId = await CategoryIdAsync("MEMORIAL"), venueId,
            localDate = date, localStart = start, durationMinutes = 60, attendeeCount = attendees, description = "A short service with readings.",
        })).Ok().Version;
        version = (await intake.PutAsync($"/api/applications/{id}/facilities", new { version, complete = true, resources = Array.Empty<object>(), specialNeeds = "Two wheelchair spaces." })).Ok().Version;
        version = (await intake.UploadAsync($"/api/applications/{id}/documents", "affiliation.pdf", "application/pdf", Files.Pdf(),
            new Dictionary<string, string> { ["documentType"] = "ProofOfAffiliation", ["version"] = version.ToString() })).Ok().Version;
        (await intake.UploadAsync($"/api/applications/{id}/documents", "plan.pdf", "application/pdf", Files.Pdf(),
            new Dictionary<string, string> { ["documentType"] = "EventPlan", ["version"] = version.ToString() })).Ok();
        return new Case(id, created.Body!["reference"]!.GetValue<string>(), date, start, venueId, venueCode);
    }

    public async Task<Case> SubmittedAsync(string intakeUser = "intake.alpha", string registryRef = "REF-10001", (DateOnly, TimeOnly)? slot = null)
    {
        var c = await DraftAsync(intakeUser, registryRef, slot: slot);
        using var intake = await app.SignInAsync(intakeUser);
        (await intake.PostAsync($"/api/applications/{c.Id}/submit", new { version = await VersionAsync(c.Id) })).Ok();
        return c;
    }

    public async Task<Case> EligibleAsync(string reviewer = "reviewer.charlie", (DateOnly, TimeOnly)? slot = null)
    {
        var c = await SubmittedAsync(slot: slot);
        using var r = await app.SignInAsync(reviewer);
        (await r.PostAsync($"/api/applications/{c.Id}/verify-registry")).Ok();
        (await r.PostAsync($"/api/applications/{c.Id}/eligibility/pass", new { version = await VersionAsync(c.Id), checklist = await ChecklistAsync(c.Id) })).Ok();
        return c;
    }

    public async Task<Dictionary<string, bool>> ChecklistAsync(Guid id) => await app.QueryAsync(async db =>
        (await db.Events.Include(e => e.Category).FirstAsync(e => e.ApplicationId == id)).Category!.ReviewChecklist.ToDictionary(i => i, _ => true));

    public async Task<Case> ReservedAsync(string reviewer = "reviewer.charlie")
    {
        var c = await EligibleAsync(reviewer);
        using var s = await app.SignInAsync("scheduler.delta");
        (await s.PostAsync($"/api/applications/{c.Id}/reservation", new
        {
            version = await VersionAsync(c.Id), venueId = c.VenueId, localDate = c.Date, localStart = c.Start, durationMinutes = 60, attendeeCount = 40,
            resources = Array.Empty<object>(), attendantNeeds = "One attendant at the chapel door.",
        })).Ok();
        return c;
    }

    /// <summary>Pending approval with the calendar hold pushed and a sync after it (so T8 is possible).</summary>
    public async Task<Case> PendingApprovalAsync(string reviewer = "reviewer.charlie", bool reconcile = true)
    {
        var c = await ReservedAsync(reviewer);
        using var s = await app.SignInAsync("scheduler.delta");
        (await s.PostAsync($"/api/applications/{c.Id}/submit-for-approval", new { version = await VersionAsync(c.Id) })).Ok();
        if (reconcile) await ReconcileCalendarAsync();
        return c;
    }

    public async Task ReconcileCalendarAsync() => await app.WithServiceAsync(async sp =>
    {
        var calendar = sp.GetRequiredService<CalendarSyncService>();
        await calendar.ProcessOutboxAsync(CancellationToken.None);
        await calendar.SyncAsync("test", Actor.System, CancellationToken.None);
        return 0;
    });

    public async Task<Case> ApprovedAsync()
    {
        var c = await PendingApprovalAsync();
        using var m = await app.SignInAsync("manager.echo");
        (await m.PostAsync($"/api/applications/{c.Id}/approve", new { version = await VersionAsync(c.Id), notes = "Approved for the practice journey." })).Ok();
        return c;
    }

    public async Task<Case> PermittedAsync()
    {
        var c = await ApprovedAsync();
        using var m = await app.SignInAsync("manager.echo");
        (await m.PostAsync($"/api/applications/{c.Id}/permit", new { version = await VersionAsync(c.Id) })).Ok();
        (await m.PostAsync($"/api/applications/{c.Id}/attendants", new { version = await VersionAsync(c.Id), userId = app.UserIds["attendant.foxtrot"], assign = true })).Ok();
        return c;
    }

    public async Task<string> ShortCodeAsync(Guid id) => await app.QueryAsync(async db =>
    {
        var permit = await db.Permits.Include(p => p.Versions).FirstAsync(p => p.ApplicationId == id);
        return permit.Versions.First(v => v.VersionNumber == permit.CurrentVersion).ShortCode;
    });

    /// <summary>Moves the app clock inside the check-in window of the event (30 minutes before the start).</summary>
    public async Task EnterCheckInWindowAsync(Guid id)
    {
        var start = await app.QueryAsync(db => db.Reservations.Where(r => r.ApplicationId == id && r.Status != ReservationStatus.Released).Select(r => r.StartUtc).FirstAsync());
        app.Clock.Offset = start.AddMinutes(-30) - DateTimeOffset.UtcNow;
    }

    public static JsonNode Action(ApiResult detail, string id) => detail.Body!["actions"]!.AsArray().First(a => a!["id"]!.GetValue<string>() == id)!;
}
