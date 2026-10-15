using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public sealed record CemeteryOption(Guid Id, string Name, string TimeZoneId);

public sealed record ReadinessView(Guid Id, string Label, bool Done, string? DoneBy, DateTimeOffset? DoneAt);

public sealed record CheckInView(DateTimeOffset CheckedInAt, string AttendantName, DateTimeOffset? CheckedOutAt, string Notes);

/// <summary>Everything the roster and check-in pages show for one event: readiness items, the check-in record and closure blockers.</summary>
public sealed record EventDayDetail(Guid ApplicationId, string Reference, string Title, CaseStatus Status, uint Version, string VenueName,
    DateTimeOffset? StartUtc, DateTimeOffset? EndUtc, string? TimeZoneId, string? PermitNumber, bool AssignedToYou,
    IReadOnlyList<ReadinessView> Readiness, CheckInView? CheckIn, IReadOnlyList<string> BlockingIncidents);

public sealed record IncidentTarget(Guid ApplicationId, string Reference, string Title, CaseStatus Status, string VenueName, DateTimeOffset? StartUtc, string? TimeZoneId, uint Version);

public sealed record ClosureRow(Guid ApplicationId, string Reference, string Title, DateTimeOffset? StartUtc, string? TimeZoneId, IReadOnlyList<string> BlockingIncidents, uint Version);

/// <summary>Read queries for the event-day pages (13 roster, 14 check-in, 15 incidents).</summary>
public sealed partial class OperationsService
{
    public async Task<IReadOnlyList<CemeteryOption>> CemeteriesAsync(Actor actor, CancellationToken ct = default)
    {
        if (!actor.Can(Cap.ViewRoster) && !actor.Can(Cap.ViewIncidents) && !actor.Can(Cap.VerifyPermitToken)) throw new ForbiddenException();
        await using var db = await Dbs.CreateAsync(actor, ct);
        return await db.Cemeteries.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).Select(c => new CemeteryOption(c.Id, c.Name, c.TimeZoneId)).ToListAsync(ct);
    }

    /// <summary>Readiness checklist, check-in record and closure blockers of one in-scope event (404 when out of scope).</summary>
    public async Task<EventDayDetail> EventDayAsync(Actor actor, Guid applicationId, CancellationToken ct = default)
    {
        if (!actor.Can(Cap.ViewRoster) && !actor.Can(Cap.VerifyPermitToken)) throw new ForbiddenException();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await Scoped(db, actor).AsNoTracking()
            .Include(a => a.Event!).ThenInclude(e => e!.Cemetery).Include(a => a.Permit).Include(a => a.ReadinessItems).Include(a => a.Incidents)
            .FirstOrDefaultAsync(a => a.Id == applicationId, ct) ?? throw new NotFoundException();
        var reservation = await db.Reservations.AsNoTracking().Include(r => r.Venue)
            .Where(r => r.ApplicationId == app.Id && r.Status != ReservationStatus.Released).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        var checkIn = await db.CheckIns.AsNoTracking().Where(c => c.ApplicationId == app.Id).OrderByDescending(c => c.CheckedInAt).FirstOrDefaultAsync(ct);
        var assigned = await db.AttendantAssignments.AsNoTracking().AnyAsync(a => a.ApplicationId == app.Id && a.UserId == actor.UserId && a.Active, ct);
        return new EventDayDetail(app.Id, app.Reference, app.Event?.Title ?? "", app.Status, app.Version, reservation?.Venue?.Name ?? "",
            reservation?.StartUtc ?? app.Event?.StartUtc, reservation?.EndUtc, app.Event?.Cemetery?.TimeZoneId, app.Permit?.Number, assigned,
            app.ReadinessItems.OrderBy(i => i.Order).Select(i => new ReadinessView(i.Id, i.Label, i.Done, i.DoneBy, i.DoneAt)).ToList(),
            checkIn is null ? null : new CheckInView(checkIn.CheckedInAt, checkIn.AttendantName, checkIn.CheckedOutAt, checkIn.Notes),
            app.Incidents.Where(i => i.BlocksClosure).OrderBy(i => i.Reference).Select(i => $"{i.Reference} ({i.Severity})").ToList());
    }

    /// <summary>Completed events waiting for closure (T13), with the High/Critical incidents that still block each one.</summary>
    public async Task<IReadOnlyList<ClosureRow>> ClosureQueueAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.CloseCases);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var apps = await Scoped(db, actor).AsNoTracking().Where(a => a.Status == CaseStatus.EventCompleted)
            .Include(a => a.Event!).ThenInclude(e => e!.Cemetery).Include(a => a.Incidents)
            .OrderBy(a => a.Event!.StartUtc).Take(50).ToListAsync(ct);
        return apps.Select(a => new ClosureRow(a.Id, a.Reference, a.Event?.Title ?? "", a.Event?.StartUtc, a.Event?.Cemetery?.TimeZoneId,
            a.Incidents.Where(i => i.BlocksClosure).OrderBy(i => i.Reference).Select(i => $"{i.Reference} ({i.Severity})").ToList(), a.Version)).ToList();
    }
}

public sealed partial class IncidentService
{
    /// <summary>Events an incident can be logged against: permitted, checked-in or completed, within the caller's scope (attendants: assigned only).</summary>
    public async Task<IReadOnlyList<IncidentTarget>> TargetsAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ReportIncidents);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var query = Scoped(db, actor).AsNoTracking().Where(a => IncidentStatuses.Contains(a.Status));
        if (actor.Has(Roles.EventAttendant) && !actor.Has(Roles.OperationsManager))
        {
            var userId = actor.UserId;
            query = query.Where(a => db.AttendantAssignments.Any(x => x.ApplicationId == a.Id && x.UserId == userId && x.Active));
        }
        var rows = await query.Include(a => a.Event!).ThenInclude(e => e!.Cemetery).Include(a => a.Event!).ThenInclude(e => e!.PreferredVenue)
            .OrderBy(a => a.Event!.StartUtc).Take(200).ToListAsync(ct);
        return rows.Select(a => new IncidentTarget(a.Id, a.Reference, a.Event?.Title ?? "", a.Status, a.Event?.PreferredVenue?.Name ?? "", a.Event?.StartUtc,
            a.Event?.Cemetery?.TimeZoneId, a.Version)).ToList();
    }
}
