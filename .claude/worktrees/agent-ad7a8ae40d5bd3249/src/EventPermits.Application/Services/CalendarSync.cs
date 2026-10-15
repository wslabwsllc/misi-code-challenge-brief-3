using System.Text.Json;
using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public sealed record CalendarSyncOverview(CalendarTokenStatus Token, CalendarSyncRun? LastRun, CalendarSyncRun? LastSuccess, string? Cursor,
    IReadOnlyList<CalendarSyncRun> Runs, IReadOnlyList<ExternalEventRow> Events, IReadOnlyList<ReconciliationRow> Queue, IReadOnlyList<ReservationPushRow> Holds,
    IReadOnlyList<IntegrationAttempt> RecentAttempts);

public sealed record ExternalEventRow(string ExternalId, string VenueCode, string? VenueName, string Title, DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset ModifiedUtc, bool Own, bool Conflicts);
public sealed record ReconciliationRow(Guid Id, Guid ApplicationId, string Reference, Guid ReservationId, string Source, DateTimeOffset? ConflictStartUtc, DateTimeOffset? ConflictEndUtc,
    IReadOnlyList<DateTimeOffset> Alternatives, ReconciliationStatus Status, string? Resolution, string? ResolvedBy, DateTimeOffset? ResolvedAt, DateTimeOffset CreatedAt,
    string VenueName, string TimeZoneId, DateTimeOffset ReservationStartUtc, CalendarPushState PushState, uint CaseVersion);
public sealed record ReservationPushRow(Guid ReservationId, Guid ApplicationId, string Reference, string VenueName, DateTimeOffset StartUtc, CalendarPushState State, DateTimeOffset? PushedAt, string? ExternalId);

/// <summary>Regional calendar integration (brief §7.1, E6-07): outbox pushes, scheduled sync, reconciliation queue.</summary>
public sealed partial class CalendarSyncService(AppDb dbs, ICorrelationContext correlation, IRegionalCalendar calendar, SchedulingService scheduling)
    : ServiceBase(dbs, correlation)
{
    private const string CursorKey = "calendar.lastSyncCursor";

    /// <summary>Worker: processes due calendar outbox messages (push and release) with backoff on failure.</summary>
    public async Task<int> ProcessOutboxAsync(CancellationToken ct)
    {
        await using var db = await Dbs.CreateAsync(ct);
        var due = await db.OutboxMessages.Where(m => m.Status == OutboxStatus.Pending && m.NextAttemptAt <= Now
                                                      && (m.Type == SchedulingService.CalendarPushMessage || m.Type == SchedulingService.CalendarReleaseMessage))
            .OrderBy(m => m.CreatedAt).Take(20).ToListAsync(ct);
        foreach (var message in due)
        {
            AmbientCorrelation.Current = message.CorrelationId;
            var payload = JsonSerializer.Deserialize<Payload>(message.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            bool done;
            string? error;
            if (message.Type == SchedulingService.CalendarPushMessage) (done, error) = await PushAsync(db, payload.ReservationId, Actor.System, ct);
            else (done, error) = await ReleaseAsync(db, payload.ReservationId, payload.ApplicationId, ct);
            message.Attempts++;
            if (done)
            {
                message.Status = OutboxStatus.Done;
                message.ProcessedAt = Now;
                message.LastError = null;
            }
            else
            {
                message.LastError = error;
                message.NextAttemptAt = Now.AddSeconds(Math.Min(600, 15 * Math.Pow(2, message.Attempts)));
                if (message.Attempts >= 12) message.Status = OutboxStatus.Failed;
            }
            await db.SaveChangesAsync(ct);
        }
        return due.Count;
    }

    /// <summary>"Retry push" on page 21: pushes one reservation immediately.</summary>
    public async Task RetryPushAsync(Actor actor, Guid reservationId, CancellationToken ct = default)
    {
        actor.Require(Cap.SyncCalendar);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var reservation = await db.Reservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reservationId, ct) ?? throw new NotFoundException();
        _ = await LoadCaseAsync(db, actor, reservation.ApplicationId, ct);
        var (_, error) = await PushAsync(db, reservationId, actor, ct);
        await db.SaveChangesAsync(ct);
        if (error is not null && !error.StartsWith("Conflict", StringComparison.Ordinal))
            throw new DependencyUnavailableException(ErrorCodes.CalendarUnavailable, error);
    }

    private async Task<(bool Done, string? Error)> PushAsync(AppDbContext db, Guid reservationId, Actor actor, CancellationToken ct)
    {
        var reservation = await db.Reservations.Include(r => r.Venue).FirstOrDefaultAsync(r => r.Id == reservationId, ct);
        if (reservation is null || reservation.Status == ReservationStatus.Released) return (true, null);
        var app = await db.Applications.Include(a => a.Event).FirstAsync(a => a.Id == reservation.ApplicationId, ct);
        var result = await calendar.PushReservationAsync(reservation.Id, app.Id, reservation.Venue!.Code, reservation.BlockStartUtc, reservation.BlockEndUtc,
            $"{app.Reference} {app.Event?.Title}", ct);
        if (result.Ok)
        {
            reservation.CalendarState = CalendarPushState.Pushed;
            reservation.ExternalReservationId = result.Value!.ExternalId;
            reservation.CalendarPushedAt = Now;
            foreach (var open in await db.ReconciliationItems.Where(r => r.ReservationId == reservation.Id && r.Status == ReconciliationStatus.Open).ToListAsync(ct))
            {
                open.Status = ReconciliationStatus.Resolved;
                open.Resolution = "Hold accepted by the regional calendar.";
                open.ResolvedBy = actor.DisplayName;
                open.ResolvedAt = Now;
            }
            Audit.Write(db, actor, Correlation, "Calendar.HoldPushed", nameof(Reservation), reservation.Id, $"Hold {result.Value.ExternalId} accepted by the regional calendar", app.Id);
            return (true, null);
        }
        if (result.Status == ExternalCallStatus.Conflict && result.Value?.Conflict is { } conflict)
        {
            reservation.CalendarState = CalendarPushState.Conflict;
            if (!await db.ReconciliationItems.AnyAsync(r => r.ReservationId == reservation.Id && r.Status == ReconciliationStatus.Open, ct))
                db.ReconciliationItems.Add(new ReconciliationItem
                {
                    ApplicationId = app.Id,
                    ReservationId = reservation.Id,
                    ExternalEventId = conflict.ExternalId,
                    SourceDescription = $"{conflict.ExternalId} · {conflict.Title}",
                    ConflictStartUtc = conflict.StartUtc,
                    ConflictEndUtc = conflict.EndUtc,
                    Alternatives = conflict.Alternatives.ToList(),
                });
            Audit.Write(db, actor, Correlation, "Calendar.Conflict", nameof(Reservation), reservation.Id, $"Regional calendar conflict with {conflict.ExternalId}; approval blocked until reconciled", app.Id);
            return (true, "Conflict reported by the regional calendar.");
        }
        reservation.CalendarState = CalendarPushState.Failed;
        Audit.Write(db, actor, Correlation, "Calendar.PushFailed", nameof(Reservation), reservation.Id, $"Hold not placed: {result.Detail}", app.Id);
        return (false, result.Detail);
    }

    private async Task<(bool, string?)> ReleaseAsync(AppDbContext db, Guid reservationId, Guid applicationId, CancellationToken ct)
    {
        var result = await calendar.ReleaseReservationAsync(reservationId, applicationId, ct);
        if (result.Ok || result.Status == ExternalCallStatus.NotFound)
        {
            Audit.Write(db, Actor.System, Correlation, "Calendar.HoldReleased", nameof(Reservation), reservationId, "Hold released in the regional calendar", applicationId);
            return (true, null);
        }
        return (false, result.Detail);
    }

    /// <summary>Imports events since the stored cursor, dedupes by externalId, raises reconciliation items for new overlaps.</summary>
    public async Task<CalendarSyncRun> SyncAsync(string trigger, Actor actor, CancellationToken ct = default)
    {
        await using var db = await Dbs.CreateAsync(actor, ct);
        var cursor = await db.SystemStates.AsNoTracking().Where(s => s.Key == CursorKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        var run = new CalendarSyncRun { StartedAt = Now, Trigger = trigger, CursorBefore = cursor };
        db.CalendarSyncRuns.Add(run);
        var result = await calendar.GetEventsAsync(Now.AddDays(-7), Now.AddDays(120), cursor, ct);
        if (!result.Ok)
        {
            run.CompletedAt = Now;
            run.Error = result.Status == ExternalCallStatus.Unauthorized ? result.Detail : $"Calendar unavailable: {result.Detail}";
            Audit.Write(db, actor, Correlation, "Calendar.SyncFailed", nameof(CalendarSyncRun), run.Id, run.Error);
            await db.SaveChangesAsync(ct);
            return run;
        }
        var venues = await db.Venues.AsNoTracking().ToDictionaryAsync(v => v.Code, v => v.Id, ct);
        foreach (var e in result.Value!.Events)
        {
            var existing = await db.ExternalCalendarEvents.FirstOrDefaultAsync(x => x.ExternalId == e.ExternalId, ct);
            Guid? own = Guid.TryParse(e.ReservationId, out var rid) ? rid : null;
            if (existing is null)
            {
                db.ExternalCalendarEvents.Add(new ExternalCalendarEvent
                {
                    ExternalId = e.ExternalId, VenueCode = e.VenueCode, VenueId = venues.GetValueOrDefault(e.VenueCode), Title = e.Title,
                    StartUtc = e.StartUtc, EndUtc = e.EndUtc, ModifiedUtc = e.ModifiedUtc, OwnReservationId = own,
                });
                run.Imported++;
            }
            else if (e.ModifiedUtc > existing.ModifiedUtc)
            {
                existing.Title = e.Title;
                existing.StartUtc = e.StartUtc;
                existing.EndUtc = e.EndUtc;
                existing.ModifiedUtc = e.ModifiedUtc;
                existing.VenueCode = e.VenueCode;
                existing.VenueId = venues.GetValueOrDefault(e.VenueCode);
                existing.OwnReservationId = own;
                run.Updated++;
            }
            else run.Unchanged++;
        }
        await db.SaveChangesAsync(ct);
        await RaiseOverlapsAsync(db, ct);

        var nextCursor = result.Value.Cursor ?? cursor;
        var state = await db.SystemStates.FirstOrDefaultAsync(s => s.Key == CursorKey, ct);
        if (state is null) db.SystemStates.Add(new SystemState { Key = CursorKey, Value = nextCursor ?? "", UpdatedAt = Now });
        else { state.Value = nextCursor ?? ""; state.UpdatedAt = Now; }
        run.CursorAfter = nextCursor;
        run.Succeeded = true;
        run.CompletedAt = Now;
        Audit.Write(db, actor, Correlation, "Calendar.Synced", nameof(CalendarSyncRun), run.Id, $"Sync ({trigger}): {run.Imported} new, {run.Updated} updated, {run.Unchanged} unchanged");
        await db.SaveChangesAsync(ct);
        return run;
    }

    public async Task<CalendarSyncRun> SyncNowAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.SyncCalendar);
        return await SyncAsync("manual", actor, ct);
    }

    /// <summary>Active holds overlapping an imported external event get an open reconciliation item (approval blocked).</summary>
    private async Task RaiseOverlapsAsync(AppDbContext db, CancellationToken ct)
    {
        var holds = await db.Reservations.Where(r => r.Status != ReservationStatus.Released && r.BlockEndUtc > Now).ToListAsync(ct);
        foreach (var hold in holds)
        {
            var clash = await db.ExternalCalendarEvents.AsNoTracking()
                .Where(e => e.VenueId == hold.VenueId && !e.Cancelled && e.OwnReservationId == null && e.StartUtc < hold.BlockEndUtc && e.EndUtc > hold.BlockStartUtc)
                .OrderBy(e => e.StartUtc).FirstOrDefaultAsync(ct);
            if (clash is null || await db.ReconciliationItems.AnyAsync(r => r.ReservationId == hold.Id && r.Status == ReconciliationStatus.Open, ct)) continue;
            db.ReconciliationItems.Add(new ReconciliationItem
            {
                ApplicationId = hold.ApplicationId, ReservationId = hold.Id, ExternalEventId = clash.ExternalId,
                SourceDescription = $"{clash.ExternalId} · {clash.Title} (imported by sync)", ConflictStartUtc = clash.StartUtc, ConflictEndUtc = clash.EndUtc,
            });
            hold.CalendarState = CalendarPushState.Conflict;
            Audit.Write(db, Actor.System, Correlation, "Calendar.Conflict", nameof(Reservation), hold.Id, $"Sync found overlapping regional event {clash.ExternalId}", hold.ApplicationId);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Reconciliation: move the reservation to an alternative slot (re-validated, then pushed again).</summary>
    public async Task ChooseAlternativeAsync(Actor actor, Guid itemId, DateTimeOffset startUtc, uint? caseVersion, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageReservations);
        Reservation reservation;
        await using (var db = await Dbs.CreateAsync(actor, ct))
        {
            var item = await db.ReconciliationItems.AsNoTracking().FirstOrDefaultAsync(r => r.Id == itemId && r.Status == ReconciliationStatus.Open, ct) ?? throw new NotFoundException();
            reservation = await db.Reservations.AsNoTracking().Include(r => r.Allocations).Include(r => r.Venue!).ThenInclude(v => v!.Cemetery)
                .FirstAsync(r => r.Id == item.ReservationId, ct);
        }
        var zone = CaseService.Zone(reservation.Venue!.Cemetery!.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(startUtc, zone);
        // Alternatives from the calendar describe the buffered block; the event starts after the setup buffer.
        var eventStart = local.AddMinutes(reservation.Venue.SetupBufferMinutes);
        await scheduling.ReserveAsync(actor, reservation.ApplicationId, caseVersion, new ReserveInput(reservation.VenueId, DateOnly.FromDateTime(eventStart.DateTime),
            TimeOnly.FromDateTime(eventStart.DateTime), (int)(reservation.EndUtc - reservation.StartUtc).TotalMinutes, reservation.AttendeeCount,
            reservation.Allocations.Where(a => !a.Released).Select(a => new ResourceRequestInput(a.ResourceId, a.Quantity)).ToList(), reservation.AttendantNeeds), ct);
        await using var after = await Dbs.CreateAsync(actor, ct);
        var open = await after.ReconciliationItems.FirstAsync(r => r.Id == itemId, ct);
        open.Status = ReconciliationStatus.Resolved;
        open.Resolution = $"Moved to alternative slot {startUtc:yyyy-MM-dd HH:mm} UTC; new hold pushed.";
        open.ResolvedBy = actor.DisplayName;
        open.ResolvedAt = Now;
        AuditLog(after, actor, "Calendar.Reconciled", nameof(ReconciliationItem), open.Id, open.Resolution, open.ApplicationId);
        await after.SaveChangesAsync(ct);
    }

    /// <summary>Close an item once the hold has been accepted by a later push (conflict settled with the other organiser).</summary>
    public async Task ResolveAsync(Actor actor, Guid itemId, string? reason, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageReservations);
        new Validator().Text("reason", reason, Limits.ReasonMin, Limits.ReasonMax, "Resolution reason").ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var item = await db.ReconciliationItems.FirstOrDefaultAsync(r => r.Id == itemId && r.Status == ReconciliationStatus.Open, ct) ?? throw new NotFoundException();
        var reservation = await db.Reservations.FirstAsync(r => r.Id == item.ReservationId, ct);
        var stillClashes = item.ExternalEventId is { } ext && await db.ExternalCalendarEvents.AnyAsync(e => e.ExternalId == ext && !e.Cancelled
            && e.StartUtc < reservation.BlockEndUtc && e.EndUtc > reservation.BlockStartUtc, ct);
        if (stillClashes)
            throw new BusinessRuleException(ErrorCodes.CalendarNotReconciled, "The external event still overlaps this reservation. Choose an alternative slot or sync again after the other organiser moves.");
        item.Status = ReconciliationStatus.Resolved;
        item.Resolution = reason!.Trim();
        item.ResolvedBy = actor.DisplayName;
        item.ResolvedAt = Now;
        reservation.CalendarState = CalendarPushState.Pending;
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = SchedulingService.CalendarPushMessage,
            Payload = JsonSerializer.Serialize(new { reservationId = reservation.Id, applicationId = reservation.ApplicationId }),
            NextAttemptAt = Now, CorrelationId = Correlation.CorrelationId,
        });
        AuditLog(db, actor, "Calendar.Reconciled", nameof(ReconciliationItem), item.Id, $"Resolved: {item.Resolution}; hold will be pushed again", item.ApplicationId, reason: item.Resolution);
        await SaveAsync(db, ct);
    }

    public async Task<CalendarSyncOverview> OverviewAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.SyncCalendar);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var runs = await db.CalendarSyncRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(15).ToListAsync(ct);
        var lastSuccess = await db.CalendarSyncRuns.AsNoTracking().Where(r => r.Succeeded).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
        var cursor = await db.SystemStates.AsNoTracking().Where(s => s.Key == CursorKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        var venues = await db.Venues.AsNoTracking().Include(v => v.Cemetery).ToDictionaryAsync(v => v.Id, ct);
        var events = await db.ExternalCalendarEvents.AsNoTracking().Where(e => e.EndUtc > Now.AddDays(-1)).OrderBy(e => e.StartUtc).Take(100).ToListAsync(ct);
        var holds = await db.Reservations.AsNoTracking().Where(r => r.Status != ReservationStatus.Released && r.EndUtc > Now.AddDays(-1))
            .Join(db.Applications, r => r.ApplicationId, a => a.Id, (r, a) => new { r, a.Reference }).OrderBy(x => x.r.StartUtc).Take(100).ToListAsync(ct);
        var queue = await db.ReconciliationItems.AsNoTracking().OrderBy(r => r.Status).ThenByDescending(r => r.CreatedAt).Take(50)
            .Join(db.Reservations, i => i.ReservationId, r => r.Id, (i, r) => new { i, r })
            .Join(db.Applications, x => x.i.ApplicationId, a => a.Id, (x, a) => new { x.i, x.r, a.Reference, a.Version }).ToListAsync(ct);
        var attempts = await db.IntegrationAttempts.AsNoTracking().Where(a => a.Integration == IntegrationKind.Calendar).OrderByDescending(a => a.At).Take(15).ToListAsync(ct);
        var holdBlocks = holds.Select(h => h.r).ToList();
        return new CalendarSyncOverview(calendar.TokenStatus, runs.FirstOrDefault(), lastSuccess, cursor, runs,
            events.Select(e => new ExternalEventRow(e.ExternalId, e.VenueCode, e.VenueId is { } v && venues.TryGetValue(v, out var vv) ? vv.Name : null, e.Title, e.StartUtc, e.EndUtc, e.ModifiedUtc,
                e.OwnReservationId is not null,
                e.OwnReservationId is null && holdBlocks.Any(h => h.VenueId == e.VenueId && h.BlockStartUtc < e.EndUtc && h.BlockEndUtc > e.StartUtc))).ToList(),
            queue.Select(x => new ReconciliationRow(x.i.Id, x.i.ApplicationId, x.Reference, x.i.ReservationId, x.i.SourceDescription, x.i.ConflictStartUtc, x.i.ConflictEndUtc,
                x.i.Alternatives, x.i.Status, x.i.Resolution, x.i.ResolvedBy, x.i.ResolvedAt, x.i.CreatedAt,
                venues.TryGetValue(x.r.VenueId, out var rv) ? rv.Name : "", venues.TryGetValue(x.r.VenueId, out var rv2) ? rv2.Cemetery!.TimeZoneId : "UTC",
                x.r.StartUtc, x.r.CalendarState, x.Version)).ToList(),
            holds.Select(h => new ReservationPushRow(h.r.Id, h.r.ApplicationId, h.Reference, venues.TryGetValue(h.r.VenueId, out var hv) ? hv.Name : "", h.r.StartUtc, h.r.CalendarState, h.r.CalendarPushedAt, h.r.ExternalReservationId)).ToList(),
            attempts);
    }

    private sealed record Payload(Guid ReservationId, Guid ApplicationId);
}
