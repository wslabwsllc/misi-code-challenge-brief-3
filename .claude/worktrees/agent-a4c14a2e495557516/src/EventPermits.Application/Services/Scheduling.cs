using System.Text.Json;
using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public sealed record SlotCheckInput(Guid VenueId, DateOnly LocalDate, TimeOnly LocalStart, int DurationMinutes, int AttendeeCount, string? CategoryCode,
    IReadOnlyList<ResourceRequestInput>? Resources, Guid? ExcludeApplicationId);

public sealed record SlotCheckResult(DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset BlockStartUtc, DateTimeOffset BlockEndUtc,
    IReadOnlyList<Conflict> Conflicts, IReadOnlyList<DateTimeOffset> Suggestions, string TimeZoneId);

public sealed record ReserveInput(Guid VenueId, DateOnly LocalDate, TimeOnly LocalStart, int DurationMinutes, int AttendeeCount,
    IReadOnlyList<ResourceRequestInput> Resources, string? AttendantNeeds);

public sealed record DayAvailability(VenueOption Venue, DateOnly Date, string TimeZoneId, OpeningHours? Hours, int SetupBuffer, int TeardownBuffer,
    IReadOnlyList<BusyItem> Items);

public sealed record BusyItem(ConflictKind Kind, string Label, DateTimeOffset StartUtc, DateTimeOffset EndUtc, Guid? ApplicationId, string? Reference, CaseStatus? Status);

public sealed record CalendarItem(string Kind, string Label, DateTimeOffset StartUtc, DateTimeOffset EndUtc, Guid VenueId, string VenueName, string TimeZoneId,
    Guid? ApplicationId, string? Reference, CaseStatus? Status);

public sealed record ResourceLine(Guid ResourceId, string Code, string Name, string Unit, int Total, int Allocated, int Remaining, IReadOnlyList<ResourceUse> Uses);
public sealed record ResourceUse(Guid AllocationId, Guid ReservationId, Guid ApplicationId, string Reference, CaseStatus Status, string VenueName,
    DateTimeOffset BlockStartUtc, DateTimeOffset BlockEndUtc, int Quantity, bool Conflict);

public sealed record EligibleCase(Guid Id, string Reference, string Title, string Applicant, Guid? VenueId, DateTimeOffset? StartUtc, int? Duration, int? Attendees, string? CategoryCode, uint Version);

/// <summary>Conflict checks, reservations, approval hand-off, resources and calendar data (FR-05..FR-07, E6-02..E6-06).</summary>
public sealed partial class SchedulingService(AppDb dbs, ICorrelationContext correlation, RuleService rules)
    : ServiceBase(dbs, correlation)
{
    public const string CalendarPushMessage = "calendar.push";
    public const string CalendarReleaseMessage = "calendar.release";

    public async Task<SlotCheckResult> CheckAsync(Actor actor, SlotCheckInput input, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewSchedule);
        await using var db = await Dbs.CreateAsync(actor, ct);
        return await CheckAsync(db, input, excludeReservation: null, ct);
    }

    private async Task<SlotCheckResult> CheckAsync(AppDbContext db, SlotCheckInput input, Guid? excludeReservation, CancellationToken ct)
    {
        var venue = await db.Venues.AsNoTracking().Include(v => v.Cemetery).FirstOrDefaultAsync(v => v.Id == input.VenueId, ct) ?? throw new NotFoundException();
        var facts = Facts(venue);
        var start = CaseService.ToUtc(input.LocalDate, input.LocalStart, venue.Cemetery!.TimeZoneId);
        var (blockStart, blockEnd) = ConflictEngine.Block(facts, start, input.DurationMinutes);
        var dayStart = start.AddHours(-30);
        var dayEnd = start.AddHours(30);
        var busy = await BusyAsync(db, venue.Id, dayStart, dayEnd, excludeReservation, input.ExcludeApplicationId, ct);
        var demands = await DemandsAsync(db, venue.CemeteryId, blockStart, blockEnd, input.Resources ?? [], excludeReservation, ct);
        var minDuration = await rules.IntAsync(RuleKeys.MinDurationMinutes, ct);
        var maxDuration = await rules.IntAsync(RuleKeys.MaxDurationMinutes, ct);
        var slot = new SlotRequest(start, input.DurationMinutes, input.AttendeeCount, input.CategoryCode, demands);
        var conflicts = ConflictEngine.Check(facts, slot, busy.Select(b => new BusyWindow(b.Kind, b.ApplicationId ?? Guid.Empty, b.Label, b.StartUtc, b.EndUtc)), Now, minDuration, maxDuration);
        var suggestions = conflicts.Count == 0 ? [] : ConflictEngine.SuggestFreeSlots(facts, slot,
            busy.Select(b => new BusyWindow(b.Kind, b.ApplicationId ?? Guid.Empty, b.Label, b.StartUtc, b.EndUtc)).ToList(), Now, 3, minDuration, maxDuration);
        return new SlotCheckResult(start, start.AddMinutes(input.DurationMinutes), blockStart, blockEnd, conflicts, suggestions, venue.Cemetery.TimeZoneId);
    }

    public async Task<DayAvailability> DayAsync(Actor actor, Guid venueId, DateOnly date, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewSchedule);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var venue = await db.Venues.AsNoTracking().Include(v => v.Cemetery).FirstOrDefaultAsync(v => v.Id == venueId, ct) ?? throw new NotFoundException();
        var start = CaseService.ToUtc(date, TimeOnly.MinValue, venue.Cemetery!.TimeZoneId);
        var items = await BusyAsync(db, venueId, start, start.AddDays(1), null, null, ct);
        return new DayAvailability(new VenueOption(venue.Id, venue.Code, venue.Name, venue.CemeteryId, venue.Cemetery.Name, venue.Cemetery.TimeZoneId, venue.Capacity, venue.AllowedCategoryCodes, venue.IsActive),
            date, venue.Cemetery.TimeZoneId, venue.OpeningHours.FirstOrDefault(h => h.Day == date.DayOfWeek), venue.SetupBufferMinutes, venue.TeardownBufferMinutes,
            items.OrderBy(i => i.StartUtc).ToList());
    }

    public async Task<IReadOnlyList<EligibleCase>> EligibleCasesAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageReservations);
        await using var db = await Dbs.CreateAsync(actor, ct);
        return await db.Applications.AsNoTracking().Where(a => a.Status == CaseStatus.Eligible || a.Status == CaseStatus.TentativelyReserved)
            .OrderBy(a => a.Event!.StartUtc)
            .Select(a => new EligibleCase(a.Id, a.Reference, a.Event!.Title, a.Applicant!.FullName, a.Event.PreferredVenueId, a.Event.StartUtc, a.Event.DurationMinutes,
                a.Event.AttendeeCount, a.Event.Category!.Code, a.Version)).ToListAsync(ct);
    }

    /// <summary>
    /// T6 (or a change of the tentative reservation). A per-venue and per-cemetery advisory lock serialises
    /// competing reservations, so two officers cannot book the same slot or stock (E6-02 AC3, E6-06 AC2).
    /// </summary>
    public async Task<CaseRef> ReserveAsync(Actor actor, Guid applicationId, uint? version, ReserveInput input, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageReservations);
        var v = new Validator()
            .Require(input.DurationMinutes > 0, "durationMinutes", "Enter a duration.")
            .Require(input.AttendeeCount >= 1, "attendeeCount", "At least one attendee is required.")
            .Text("attendantNeeds", input.AttendantNeeds, 0, 500, "Attendant needs", false);
        foreach (var r in input.Resources.Where(r => r.Quantity < 0)) v.Add($"resource-{r.ResourceId}", "Quantity cannot be negative.");
        v.ThrowIfInvalid();

        await using var db = await Dbs.CreateAsync(actor, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var app = await LoadCaseAsync(db, actor, applicationId, ct, q => q.Include(a => a.Event!).ThenInclude(e => e!.Category).Include(a => a.Reservations).ThenInclude(r => r.Allocations));
        ExpectVersion(db, app, version);
        // A tentative reservation may be changed until approval (including reconciliation moves while Pending Approval).
        var change = app.Status is CaseStatus.TentativelyReserved or CaseStatus.PendingApproval;
        if (!change) RequireTransition(app, Transition.Reserve);
        // Eligibility checklist complete before reservation (brief §4): only a passed review moves a case to Eligible.
        if (!await db.EligibilityReviews.AnyAsync(r => r.ApplicationId == app.Id && r.Decision == ReviewDecision.Pass, ct))
            throw new BusinessRuleException(ErrorCodes.ChecklistIncomplete, "The eligibility checklist has not been completed for this case.");

        var venue = await db.Venues.AsNoTracking().Include(x => x.Cemetery).FirstOrDefaultAsync(x => x.Id == input.VenueId, ct) ?? throw ValidationException.For("venueId", "Choose a venue.");
        await LockAsync(db, venue.Id, ct);
        await LockAsync(db, venue.CemeteryId, ct);

        var existing = app.Reservations.FirstOrDefault(r => r.Status != ReservationStatus.Released);
        var check = await CheckAsync(db, new SlotCheckInput(input.VenueId, input.LocalDate, input.LocalStart, input.DurationMinutes, input.AttendeeCount,
            app.Event?.Category?.Code, input.Resources, app.Id), existing?.Id, ct);
        if (check.Conflicts.Count > 0)
            throw new BusinessRuleException(ErrorCodes.ReservationConflict, "The slot is not available: " + string.Join(" ", check.Conflicts.Select(c => c.Message)));

        var reservation = existing ?? new Reservation { ApplicationId = app.Id };
        var before = existing is null ? null : new { existing.VenueId, existing.StartUtc, existing.EndUtc, existing.AttendeeCount };
        reservation.VenueId = venue.Id;
        reservation.StartUtc = check.StartUtc;
        reservation.EndUtc = check.EndUtc;
        reservation.BlockStartUtc = check.BlockStartUtc;
        reservation.BlockEndUtc = check.BlockEndUtc;
        reservation.AttendeeCount = input.AttendeeCount;
        reservation.AttendantNeeds = input.AttendantNeeds?.Trim() ?? "";
        reservation.Status = ReservationStatus.Tentative;
        reservation.CalendarState = CalendarPushState.Pending;
        if (existing is null) db.Reservations.Add(reservation);

        foreach (var r in input.Resources)
        {
            var allocation = reservation.Allocations.FirstOrDefault(a => a.ResourceId == r.ResourceId && !a.Released);
            if (allocation is null && r.Quantity > 0) reservation.Allocations.Add(new ResourceAllocation { ResourceId = r.ResourceId, Quantity = r.Quantity });
            else if (allocation is not null && r.Quantity == 0) allocation.Released = true;
            else if (allocation is not null) allocation.Quantity = r.Quantity;
        }
        // The case event follows the confirmed slot.
        app.Event!.PreferredVenueId = venue.Id;
        app.Event.CemeteryId = venue.CemeteryId;
        app.Event.StartUtc = check.StartUtc;
        app.Event.DurationMinutes = input.DurationMinutes;
        app.Event.AttendeeCount = input.AttendeeCount;

        // A-27: the hold goes to the regional calendar through the outbox, committed with the reservation.
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = CalendarPushMessage,
            Payload = JsonSerializer.Serialize(new { reservationId = reservation.Id, applicationId = app.Id }),
            NextAttemptAt = Now,
            CorrelationId = Correlation.CorrelationId,
        });
        if (change)
        {
            Touch(db, app);
            AuditLog(db, actor, "Reservation.Changed", nameof(Reservation), reservation.Id, $"Reservation moved to {venue.Name} {check.StartUtc:yyyy-MM-dd HH:mm} UTC", app.Id,
                before, new { reservation.VenueId, reservation.StartUtc, reservation.EndUtc, reservation.AttendeeCount });
        }
        else
        {
            ApplyTransition(db, actor, app, Transition.Reserve, $"{venue.Name}, {check.StartUtc:yyyy-MM-dd HH:mm} UTC");
            await NotificationOutbox.QueueAsync(db, Correlation, app, "reservation.tentative", ct: ct);
        }
        await SaveAsync(db, ct);
        await tx.CommitAsync(ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>T7: the reservation summary must be complete (venue, slot, attendees, attendant needs).</summary>
    public async Task<CaseRef> SubmitForApprovalAsync(Actor actor, Guid applicationId, uint? version, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageReservations);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, applicationId, ct, q => q.Include(a => a.Reservations));
        ExpectVersion(db, app, version);
        RequireTransition(app, Transition.SubmitForApproval);
        var reservation = app.Reservations.FirstOrDefault(r => r.Status != ReservationStatus.Released);
        var missing = new List<string>();
        if (reservation is null) missing.Add("reservation");
        else
        {
            if (reservation.AttendeeCount < 1) missing.Add("attendee count");
            if (string.IsNullOrWhiteSpace(reservation.AttendantNeeds)) missing.Add("attendant needs");
        }
        if (missing.Count > 0)
            throw new BusinessRuleException(ErrorCodes.ReservationIncomplete, $"The reservation summary is incomplete: {string.Join(", ", missing)}.");
        ApplyTransition(db, actor, app, Transition.SubmitForApproval);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    public async Task<IReadOnlyList<CalendarItem>> CalendarAsync(Actor actor, DateTimeOffset fromUtc, DateTimeOffset toUtc, Guid? cemeteryId, Guid? venueId, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewSchedule);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var venues = await db.Venues.AsNoTracking().Include(v => v.Cemetery)
            .Where(v => (cemeteryId == null || v.CemeteryId == cemeteryId) && (venueId == null || v.Id == venueId)).ToDictionaryAsync(v => v.Id, ct);
        var ids = venues.Keys.ToList();
        var items = new List<CalendarItem>();
        var reservations = await db.Reservations.AsNoTracking().Where(r => ids.Contains(r.VenueId) && r.Status != ReservationStatus.Released && r.StartUtc < toUtc && r.EndUtc > fromUtc)
            .Join(db.Applications, r => r.ApplicationId, a => a.Id, (r, a) => new { r, a.Reference, a.Status, a.Event!.Title }).ToListAsync(ct);
        items.AddRange(reservations.Select(x => Item("reservation", $"{x.Reference} · {x.Title}", x.r.StartUtc, x.r.EndUtc, venues[x.r.VenueId], x.r.ApplicationId, x.Reference, x.Status)));
        var external = await db.ExternalCalendarEvents.AsNoTracking().Where(e => e.VenueId != null && ids.Contains(e.VenueId.Value) && !e.Cancelled && e.OwnReservationId == null && e.StartUtc < toUtc && e.EndUtc > fromUtc).ToListAsync(ct);
        items.AddRange(external.Select(e => Item("external", $"{e.ExternalId} · {e.Title}", e.StartUtc, e.EndUtc, venues[e.VenueId!.Value], null, null, null)));
        var blackouts = await db.VenueBlackouts.AsNoTracking().Where(b => ids.Contains(b.VenueId) && b.IsActive && b.StartUtc < toUtc && b.EndUtc > fromUtc).ToListAsync(ct);
        items.AddRange(blackouts.Select(b => Item("blackout", $"Closed: {b.Reason}", b.StartUtc, b.EndUtc, venues[b.VenueId], null, null, null)));
        var partner = await db.PartnerEvents.AsNoTracking().Where(p => p.VenueId != null && ids.Contains(p.VenueId.Value) && p.StartUtc < toUtc && p.EndUtc > fromUtc).ToListAsync(ct);
        items.AddRange(partner.Select(p => Item("partner", $"Partner {p.PartnerEventId} ({p.AttendeeCount})", p.StartUtc, p.EndUtc, venues[p.VenueId!.Value], null, null, null)));
        return items.OrderBy(i => i.StartUtc).ToList();
    }

    public async Task<IReadOnlyList<ResourceLine>> ResourcesAsync(Actor actor, Guid cemeteryId, DateOnly date, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewSchedule);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var cemetery = await db.Cemeteries.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cemeteryId, ct) ?? throw new NotFoundException();
        var dayStart = CaseService.ToUtc(date, TimeOnly.MinValue, cemetery.TimeZoneId);
        var dayEnd = dayStart.AddDays(1);
        var resources = await db.Resources.AsNoTracking().Where(r => r.CemeteryId == cemeteryId && r.IsActive).OrderBy(r => r.Name).ToListAsync(ct);
        var uses = await (from al in db.ResourceAllocations.AsNoTracking()
                          join r in db.Reservations.AsNoTracking() on al.ReservationId equals r.Id
                          join a in db.Applications.AsNoTracking() on r.ApplicationId equals a.Id
                          join v in db.Venues.AsNoTracking() on r.VenueId equals v.Id
                          where !al.Released && r.Status != ReservationStatus.Released && v.CemeteryId == cemeteryId && r.BlockStartUtc < dayEnd && r.BlockEndUtc > dayStart
                          select new { al, r, a.Reference, a.Status, VenueName = v.Name }).ToListAsync(ct);
        return resources.Select(res =>
        {
            var mine = uses.Where(u => u.al.ResourceId == res.Id).ToList();
            var lines = mine.Select(u =>
            {
                // Peak use while this reservation runs: overlapping allocations of the same resource.
                var peak = mine.Where(o => ConflictEngine.Overlaps(o.r.BlockStartUtc, o.r.BlockEndUtc, u.r.BlockStartUtc, u.r.BlockEndUtc)).Sum(o => o.al.Quantity);
                return new ResourceUse(u.al.Id, u.r.Id, u.r.ApplicationId, u.Reference, u.Status, u.VenueName, u.r.BlockStartUtc, u.r.BlockEndUtc, u.al.Quantity, peak > res.TotalQuantity);
            }).OrderBy(u => u.BlockStartUtc).ToList();
            var peakAll = lines.Count == 0 ? 0 : lines.Max(l => mine.Where(o => ConflictEngine.Overlaps(o.r.BlockStartUtc, o.r.BlockEndUtc, l.BlockStartUtc, l.BlockEndUtc)).Sum(o => o.al.Quantity));
            return new ResourceLine(res.Id, res.Code, res.Name, res.Unit, res.TotalQuantity, peakAll, res.TotalQuantity - peakAll, lines);
        }).ToList();
    }

    /// <summary>Change one allocation from the Resource Planner (E6-06 AC2), serialised per cemetery.</summary>
    public async Task<CaseRef> SetAllocationAsync(Actor actor, Guid reservationId, Guid resourceId, int quantity, uint? caseVersion, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageReservations);
        if (quantity < 0) throw ValidationException.For("quantity", "Quantity cannot be negative.");
        await using var db = await Dbs.CreateAsync(actor, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var reservation = await db.Reservations.Include(r => r.Allocations).Include(r => r.Venue).FirstOrDefaultAsync(r => r.Id == reservationId, ct) ?? throw new NotFoundException();
        var app = await LoadCaseAsync(db, actor, reservation.ApplicationId, ct);
        ExpectVersion(db, app, caseVersion);
        if (app.Status is not (CaseStatus.Eligible or CaseStatus.TentativelyReserved or CaseStatus.PendingApproval or CaseStatus.Approved or CaseStatus.Permitted))
            throw new BusinessRuleException(ErrorCodes.CaseClosed, $"Allocations cannot change while the case is {Workflow.Display(app.Status)}.");
        if (reservation.Status == ReservationStatus.Released) throw new BusinessRuleException(ErrorCodes.CaseClosed, "This reservation has been released.");
        var resource = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == resourceId, ct) ?? throw new NotFoundException();
        if (resource.CemeteryId != reservation.Venue!.CemeteryId) throw ValidationException.For("resourceId", "This resource is stocked at another cemetery.");
        await LockAsync(db, resource.CemeteryId, ct);
        var used = await UsedElsewhereAsync(db, resourceId, reservation.BlockStartUtc, reservation.BlockEndUtc, reservation.Id, ct);
        if (quantity > resource.TotalQuantity - used)
            throw new BusinessRuleException(ErrorCodes.ReservationConflict, $"{resource.Name}: only {Math.Max(0, resource.TotalQuantity - used)} of {resource.TotalQuantity} available for this time.");
        var allocation = reservation.Allocations.FirstOrDefault(a => a.ResourceId == resourceId && !a.Released);
        var before = allocation?.Quantity ?? 0;
        if (allocation is null && quantity > 0) reservation.Allocations.Add(new ResourceAllocation { ResourceId = resourceId, Quantity = quantity });
        else if (allocation is not null && quantity == 0) allocation.Released = true;
        else if (allocation is not null) allocation.Quantity = quantity;
        Touch(db, app);
        AuditLog(db, actor, "Allocation.Changed", nameof(Reservation), reservation.Id, $"{resource.Name}: {before} → {quantity}", app.Id);
        await SaveAsync(db, ct);
        await tx.CommitAsync(ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>Releases a case's reservation, its allocations and the external hold (cancel and rejection, E2-07, E6-06 AC3).</summary>
    public static void ReleaseAll(AppDbContext db, PermitApplication app, string reason, ICorrelationContext correlation, DateTimeOffset now)
    {
        foreach (var reservation in app.Reservations.Where(r => r.Status != ReservationStatus.Released))
        {
            reservation.Status = ReservationStatus.Released;
            reservation.ReleaseReason = reason;
            foreach (var allocation in reservation.Allocations) allocation.Released = true;
            if (reservation.CalendarState is CalendarPushState.Pushed or CalendarPushState.Pending or CalendarPushState.Failed)
                db.OutboxMessages.Add(new OutboxMessage
                {
                    Type = CalendarReleaseMessage,
                    Payload = JsonSerializer.Serialize(new { reservationId = reservation.Id, applicationId = app.Id }),
                    NextAttemptAt = now,
                    CorrelationId = correlation.CorrelationId,
                });
        }
    }

    public static VenueFacts Facts(Venue venue) => new(venue.Id, venue.IsActive, venue.Capacity, venue.SetupBufferMinutes, venue.TeardownBufferMinutes,
        venue.OpeningHours, venue.AllowedCategoryCodes, CaseService.Zone(venue.Cemetery?.TimeZoneId));

    private static async Task<List<BusyItem>> BusyAsync(AppDbContext db, Guid venueId, DateTimeOffset from, DateTimeOffset to, Guid? excludeReservation, Guid? excludeApplication, CancellationToken ct)
    {
        var items = new List<BusyItem>();
        var reservations = await db.Reservations.AsNoTracking()
            .Where(r => r.VenueId == venueId && r.Status != ReservationStatus.Released && r.BlockStartUtc < to && r.BlockEndUtc > from
                        && (excludeReservation == null || r.Id != excludeReservation) && (excludeApplication == null || r.ApplicationId != excludeApplication))
            .Join(db.Applications, r => r.ApplicationId, a => a.Id, (r, a) => new { r, a.Reference, a.Status }).ToListAsync(ct);
        items.AddRange(reservations.Select(x => new BusyItem(ConflictKind.ReservationOverlap, x.Reference, x.r.BlockStartUtc, x.r.BlockEndUtc, x.r.ApplicationId, x.Reference, x.Status)));
        var blackouts = await db.VenueBlackouts.AsNoTracking().Where(b => b.VenueId == venueId && b.IsActive && b.StartUtc < to && b.EndUtc > from).ToListAsync(ct);
        items.AddRange(blackouts.Select(b => new BusyItem(ConflictKind.Blackout, b.Reason, b.StartUtc, b.EndUtc, null, null, null)));
        // Our own pushed holds appear in the external feed too; they are the reservations counted above, not conflicts.
        var external = await db.ExternalCalendarEvents.AsNoTracking()
            .Where(e => e.VenueId == venueId && !e.Cancelled && e.OwnReservationId == null && e.StartUtc < to && e.EndUtc > from).ToListAsync(ct);
        items.AddRange(external.Select(e => new BusyItem(ConflictKind.ExternalCalendarEvent, $"{e.ExternalId} ({e.Title})", e.StartUtc, e.EndUtc, null, null, null)));
        var partner = await db.PartnerEvents.AsNoTracking().Where(p => p.VenueId == venueId && p.StartUtc < to && p.EndUtc > from).ToListAsync(ct);
        items.AddRange(partner.Select(p => new BusyItem(ConflictKind.PartnerEvent, p.PartnerEventId, p.StartUtc, p.EndUtc, null, null, null)));
        return items;
    }

    private static async Task<List<ResourceDemand>> DemandsAsync(AppDbContext db, Guid cemeteryId, DateTimeOffset blockStart, DateTimeOffset blockEnd,
        IReadOnlyList<ResourceRequestInput> requests, Guid? excludeReservation, CancellationToken ct)
    {
        var demands = new List<ResourceDemand>();
        foreach (var request in requests.Where(r => r.Quantity > 0))
        {
            var resource = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.ResourceId, ct);
            if (resource is null || resource.CemeteryId != cemeteryId)
            {
                demands.Add(new ResourceDemand(request.ResourceId, "Unknown resource", request.Quantity, 0, 0));
                continue;
            }
            var used = await UsedElsewhereAsync(db, resource.Id, blockStart, blockEnd, excludeReservation, ct);
            demands.Add(new ResourceDemand(resource.Id, resource.Name, request.Quantity, resource.TotalQuantity, used));
        }
        return demands;
    }

    private static async Task<int> UsedElsewhereAsync(AppDbContext db, Guid resourceId, DateTimeOffset blockStart, DateTimeOffset blockEnd, Guid? excludeReservation, CancellationToken ct) =>
        await (from al in db.ResourceAllocations
               join r in db.Reservations on al.ReservationId equals r.Id
               where al.ResourceId == resourceId && !al.Released && r.Status != ReservationStatus.Released
                     && r.BlockStartUtc < blockEnd && r.BlockEndUtc > blockStart && (excludeReservation == null || r.Id != excludeReservation)
               select al.Quantity).SumAsync(ct);

    /// <summary>Transaction-scoped PostgreSQL advisory lock keyed by an id (released at commit/rollback).</summary>
    private static Task LockAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var key = BitConverter.ToInt64(id.ToByteArray(), 0);
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", ct);
    }

    private static CalendarItem Item(string kind, string label, DateTimeOffset start, DateTimeOffset end, Venue venue, Guid? appId, string? reference, CaseStatus? status) =>
        new(kind, label, start, end, venue.Id, venue.Name, venue.Cemetery?.TimeZoneId ?? "UTC", appId, reference, status);
}
