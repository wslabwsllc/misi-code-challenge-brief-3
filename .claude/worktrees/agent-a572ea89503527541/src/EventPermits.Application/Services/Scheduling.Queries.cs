using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

/// <summary>One candidate start on a venue's day: free when the conflict engine finds no blocking problem.</summary>
public sealed record SlotOption(DateTimeOffset StartUtc, DateTimeOffset EndUtc, bool Free, string? Reason);

public sealed record AllocationLine(Guid ResourceId, string Name, string Unit, int Quantity);

public sealed record ReservationSummary(Guid Id, Guid VenueId, string VenueName, string TimeZoneId, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    DateTimeOffset BlockStartUtc, DateTimeOffset BlockEndUtc, int AttendeeCount, string AttendantNeeds, CalendarPushState CalendarState, bool OpenReconciliation,
    IReadOnlyList<AllocationLine> Allocations);

/// <summary>A case on the scheduling desk (Eligible, Tentatively Reserved or Pending Approval) with its current reservation.</summary>
public sealed record SchedulingCase(Guid Id, string Reference, string Title, string Applicant, CaseStatus Status, uint Version, string? CategoryCode,
    Guid? PreferredVenueId, DateTimeOffset? PreferredStartUtc, int? DurationMinutes, int? AttendeeCount, ReservationSummary? Reservation);

/// <summary>A reservation active on a cemetery's day, for the Resource Planner (allocations and attendants).</summary>
public sealed record PlannerReservation(Guid ReservationId, Guid ApplicationId, string Reference, string Title, CaseStatus Status, uint Version, Guid VenueId,
    string VenueName, string TimeZoneId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset BlockStartUtc, DateTimeOffset BlockEndUtc, int AttendeeCount,
    string AttendantNeeds, IReadOnlyList<AllocationLine> Allocations, IReadOnlyList<StaffOption> Attendants);

/// <summary>Read queries for the scheduling pages (07, 08, 09). Rules stay in <see cref="ConflictEngine"/>.</summary>
public sealed partial class SchedulingService
{
    private static readonly CaseStatus[] DeskStatuses = [CaseStatus.Eligible, CaseStatus.TentativelyReserved, CaseStatus.PendingApproval];

    /// <summary>
    /// Candidate starts across the venue's opening hours on <paramref name="date"/> (venue-local), each checked by the
    /// conflict engine against closures, reservations (with buffers), external and partner events. Attendee, category
    /// and resource checks are left to the full proposal check.
    /// </summary>
    public async Task<IReadOnlyList<SlotOption>> DaySlotsAsync(Actor actor, Guid venueId, DateOnly date, int durationMinutes, int stepMinutes = 60, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewSchedule);
        if (stepMinutes is < 15 or > 240) throw ValidationException.For("stepMinutes", "Step must be 15 to 240 minutes.");
        await using var db = await Dbs.CreateAsync(actor, ct);
        var venue = await db.Venues.AsNoTracking().Include(v => v.Cemetery).FirstOrDefaultAsync(v => v.Id == venueId, ct) ?? throw new NotFoundException();
        var hours = venue.OpeningHours.FirstOrDefault(h => h.Day == date.DayOfWeek);
        if (hours is null) return [];
        var minDuration = await rules.IntAsync(RuleKeys.MinDurationMinutes, ct);
        var maxDuration = await rules.IntAsync(RuleKeys.MaxDurationMinutes, ct);
        var duration = Math.Clamp(durationMinutes, minDuration, maxDuration);
        var facts = Facts(venue);
        var dayStart = CaseService.ToUtc(date, TimeOnly.MinValue, venue.Cemetery!.TimeZoneId);
        var busy = (await BusyAsync(db, venueId, dayStart.AddHours(-6), dayStart.AddHours(30), null, null, ct))
            .Select(b => new BusyWindow(b.Kind, b.ApplicationId ?? Guid.Empty, b.Label, b.StartUtc, b.EndUtc)).ToList();
        var result = new List<SlotOption>();
        for (var local = hours.Opens; local < hours.Closes; local = local.AddMinutes(stepMinutes))
        {
            var start = CaseService.ToUtc(date, local, venue.Cemetery.TimeZoneId);
            var problems = ConflictEngine.Check(facts, new SlotRequest(start, duration, 1, null, []), busy, Now, minDuration, maxDuration);
            result.Add(new SlotOption(start, start.AddMinutes(duration), problems.Count == 0, problems.FirstOrDefault()?.Message));
            if (local.AddMinutes(stepMinutes) <= local) break; // wrapped past midnight
        }
        return result;
    }

    /// <summary>Cases waiting for a reservation (T6), holding one (T7) or pending approval, with the reservation summary.</summary>
    public async Task<IReadOnlyList<SchedulingCase>> DeskAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewSchedule);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var cases = await Scoped(db, actor).AsNoTracking().Where(a => DeskStatuses.Contains(a.Status))
            .OrderBy(a => a.Event!.StartUtc)
            .Select(a => new
            {
                a.Id, a.Reference, Title = a.Event!.Title, Applicant = a.Applicant!.FullName, a.Status, a.Version, CategoryCode = a.Event.Category!.Code,
                a.Event.PreferredVenueId, a.Event.StartUtc, a.Event.DurationMinutes, a.Event.AttendeeCount,
            }).Take(200).ToListAsync(ct);
        var ids = cases.Select(c => c.Id).ToList();
        var reservations = await ReservationSummariesAsync(db, db.Reservations.Where(r => ids.Contains(r.ApplicationId) && r.Status != ReservationStatus.Released), ct);
        return cases.Select(c => new SchedulingCase(c.Id, c.Reference, c.Title, c.Applicant, c.Status, c.Version, c.CategoryCode, c.PreferredVenueId, c.StartUtc,
            c.DurationMinutes, c.AttendeeCount, reservations.GetValueOrDefault(c.Id))).ToList();
    }

    /// <summary>Reservations whose buffered block touches the cemetery's local day, with allocations and assigned attendants.</summary>
    public async Task<IReadOnlyList<PlannerReservation>> PlannerAsync(Actor actor, Guid cemeteryId, DateOnly date, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewSchedule);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var cemetery = await db.Cemeteries.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cemeteryId, ct) ?? throw new NotFoundException();
        var dayStart = CaseService.ToUtc(date, TimeOnly.MinValue, cemetery.TimeZoneId);
        var dayEnd = dayStart.AddDays(1);
        var scoped = Scoped(db, actor).Select(a => a.Id);
        var query = db.Reservations.Where(r => r.Status != ReservationStatus.Released && r.Venue!.CemeteryId == cemeteryId
                                               && r.BlockStartUtc < dayEnd && r.BlockEndUtc > dayStart && scoped.Contains(r.ApplicationId));
        var summaries = await ReservationSummariesAsync(db, query, ct);
        var ids = summaries.Keys.ToList();
        var cases = await db.Applications.AsNoTracking().Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Reference, Title = a.Event!.Title, a.Status, a.Version }).ToDictionaryAsync(a => a.Id, ct);
        var attendants = await db.Set<AttendantAssignment>().AsNoTracking().Where(s => ids.Contains(s.ApplicationId) && s.Active).ToListAsync(ct);
        return summaries.Select(kv =>
        {
            var (appId, r) = (kv.Key, kv.Value);
            var c = cases[appId];
            return new PlannerReservation(r.Id, appId, c.Reference, c.Title, c.Status, c.Version, r.VenueId, r.VenueName, r.TimeZoneId, r.StartUtc, r.EndUtc,
                r.BlockStartUtc, r.BlockEndUtc, r.AttendeeCount, r.AttendantNeeds, r.Allocations,
                attendants.Where(s => s.ApplicationId == appId).Select(s => new StaffOption(s.UserId, s.UserName)).OrderBy(s => s.DisplayName).ToList());
        }).OrderBy(r => r.StartUtc).ToList();
    }

    private static async Task<Dictionary<Guid, ReservationSummary>> ReservationSummariesAsync(AppDbContext db, IQueryable<Reservation> query, CancellationToken ct)
    {
        var rows = await query.AsNoTracking()
            .Select(r => new { r, VenueName = r.Venue!.Name, TimeZoneId = r.Venue.Cemetery!.TimeZoneId }).ToListAsync(ct);
        var reservationIds = rows.Select(x => x.r.Id).ToList();
        var allocations = await db.ResourceAllocations.AsNoTracking().Where(a => reservationIds.Contains(a.ReservationId) && !a.Released)
            .Select(a => new { a.ReservationId, a.ResourceId, a.Resource!.Name, a.Resource.Unit, a.Quantity }).ToListAsync(ct);
        var open = await db.ReconciliationItems.AsNoTracking().Where(i => reservationIds.Contains(i.ReservationId) && i.Status == ReconciliationStatus.Open)
            .Select(i => i.ReservationId).ToListAsync(ct);
        var result = new Dictionary<Guid, ReservationSummary>();
        foreach (var x in rows)
        {
            result[x.r.ApplicationId] = new ReservationSummary(x.r.Id, x.r.VenueId, x.VenueName, x.TimeZoneId, x.r.StartUtc, x.r.EndUtc, x.r.BlockStartUtc, x.r.BlockEndUtc,
                x.r.AttendeeCount, x.r.AttendantNeeds, x.r.CalendarState, open.Contains(x.r.Id),
                allocations.Where(a => a.ReservationId == x.r.Id).OrderBy(a => a.Name).Select(a => new AllocationLine(a.ResourceId, a.Name, a.Unit, a.Quantity)).ToList());
        }
        return result;
    }
}
