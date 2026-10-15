using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public sealed record RosterRow(Guid ApplicationId, string Reference, string Title, CaseStatus Status, string VenueName, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    string TimeZoneId, int Attendees, string ApplicantName, string ApplicantPhone, string? PermitNumber, bool PermitValid, IReadOnlyList<AttendantAssignment> Attendants,
    int ReadinessDone, int ReadinessTotal, int AllocationCount, bool WeatherAcknowledged, bool SevereWeather, int OpenIncidents, uint Version);

public sealed record StaffOption(string UserId, string DisplayName);

public sealed record IncidentInput(string? Category, IncidentSeverity Severity, string? Description, string? OwnerUserId, DateOnly? DueDate, string? CorrectiveAction);

public sealed record IncidentRow(Guid Id, string Reference, Guid ApplicationId, string CaseReference, string EventTitle, string Category, IncidentSeverity Severity, IncidentStatus Status,
    string Description, string ReportedByName, string? OwnerName, DateOnly? DueDate, bool Overdue, DateTimeOffset CreatedAt, int Photos, uint Version);

public sealed record IncidentFilter(IncidentSeverity? Severity = null, IncidentStatus? Status = null, Guid? CemeteryId = null, DateOnly? From = null, DateOnly? To = null, int Page = 1);

/// <summary>Daily roster, attendants, readiness, check-in/out, completion, closure and cancellation (FR-10, E8-01, E8-02, E8-04, E2-07).</summary>
public sealed partial class OperationsService(AppDb dbs, ICorrelationContext correlation, PermitService permits, IPdfRenderer pdf)
    : ServiceBase(dbs, correlation)
{
    private static readonly CaseStatus[] EventDayStatuses = [CaseStatus.Approved, CaseStatus.Permitted, CaseStatus.CheckedIn, CaseStatus.EventCompleted, CaseStatus.Closed];

    public async Task<IReadOnlyList<RosterRow>> RosterAsync(Actor actor, DateOnly date, Guid? cemeteryId, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewRoster);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var cemeteries = await db.Cemeteries.AsNoTracking().Where(c => cemeteryId == null || c.Id == cemeteryId).ToListAsync(ct);
        var rows = new List<RosterRow>();
        foreach (var cemetery in cemeteries)
        {
            var dayStart = CaseService.ToUtc(date, TimeOnly.MinValue, cemetery.TimeZoneId);
            var dayEnd = dayStart.AddDays(1);
            var query = Scoped(db, actor).AsNoTracking()
                .Where(a => EventDayStatuses.Contains(a.Status))
                .SelectMany(a => a.Reservations.Where(r => r.Status != ReservationStatus.Released && r.Venue!.CemeteryId == cemetery.Id && r.StartUtc >= dayStart && r.StartUtc < dayEnd),
                    (a, r) => new { a, r, VenueName = r.Venue!.Name });
            foreach (var x in await query.ToListAsync(ct))
            {
                var app = await db.Applications.AsNoTracking().Include(a => a.Applicant).Include(a => a.Event).Include(a => a.Permit!).ThenInclude(p => p.Versions)
                    .Include(a => a.Assignments).Include(a => a.ReadinessItems).Include(a => a.Incidents).FirstAsync(a => a.Id == x.a.Id, ct);
                var permit = app.Permit;
                rows.Add(new RosterRow(app.Id, app.Reference, app.Event?.Title ?? "", app.Status, x.VenueName, x.r.StartUtc, x.r.EndUtc, cemetery.TimeZoneId, x.r.AttendeeCount,
                    app.Applicant?.FullName ?? "", CaseScope.MasksContacts(actor) ? CaseScope.MaskPhone(app.Applicant?.Phone ?? "") : app.Applicant?.Phone ?? "",
                    permit?.Number, permit is { Status: PermitStatus.Active } && permit.Versions.Any(v => v.VersionNumber == permit.CurrentVersion && v.Status == PermitVersionStatus.Active),
                    app.Assignments.Where(s => s.Active).ToList(), app.ReadinessItems.Count(i => i.Done), app.ReadinessItems.Count,
                    await db.ResourceAllocations.CountAsync(al => al.ReservationId == x.r.Id && !al.Released, ct),
                    app.WeatherAcknowledgedAt is not null, await CaseFacts.SevereWeatherAsync(db, app.Id, x.r, ct),
                    app.Incidents.Count(i => i.Status != IncidentStatus.Resolved), app.Version));
            }
        }
        return rows.OrderBy(r => r.StartUtc).ToList();
    }

    public async Task<IReadOnlyList<StaffOption>> AttendantsAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.AssignAttendants);
        return await UsersInRoleAsync(Roles.EventAttendant, ct);
    }

    public async Task<IReadOnlyList<StaffOption>> ManagersAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewIncidents);
        return await UsersInRoleAsync(Roles.OperationsManager, ct);
    }

    private async Task<IReadOnlyList<StaffOption>> UsersInRoleAsync(string role, CancellationToken ct)
    {
        await using var db = await Dbs.CreateAsync(ct);
        return await (from u in db.Users
                      join ur in db.UserRoles on u.Id equals ur.UserId
                      join r in db.Roles on ur.RoleId equals r.Id
                      where r.Name == role && u.IsActive
                      orderby u.DisplayName
                      select new StaffOption(u.Id, u.DisplayName)).Distinct().ToListAsync(ct);
    }

    public async Task<CaseRef> AssignAttendantAsync(Actor actor, Guid applicationId, uint? version, string userId, bool assign, CancellationToken ct = default)
    {
        actor.Require(Cap.AssignAttendants);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, applicationId, ct, q => q.Include(a => a.Assignments));
        ExpectVersion(db, app, version);
        if (!EventDayStatuses.Take(3).Contains(app.Status))
            throw new BusinessRuleException(ErrorCodes.InvalidTransition, $"Attendants are assigned between approval and check-in; the case is {Workflow.Display(app.Status)}.");
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct) ?? throw ValidationException.For("userId", "Choose an active attendant.");
        var isAttendant = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == userId && r.Name == Roles.EventAttendant select ur).AnyAsync(ct);
        if (!isAttendant) throw ValidationException.For("userId", "The user is not an Event Attendant.");
        var existing = app.Assignments.FirstOrDefault(a => a.UserId == userId && a.Active);
        if (assign && existing is null)
            app.Assignments.Add(new AttendantAssignment { UserId = userId, UserName = user.DisplayName });
        else if (!assign && existing is not null)
            existing.Active = false;
        else return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
        Touch(db, app);
        AuditLog(db, actor, assign ? "Attendant.Assigned" : "Attendant.Unassigned", nameof(PermitApplication), app.Id, $"{user.DisplayName} {(assign ? "assigned to" : "removed from")} {app.Reference}", app.Id);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    public async Task<CaseRef> SetReadinessAsync(Actor actor, Guid itemId, bool done, uint? caseVersion, CancellationToken ct = default)
    {
        await using var db = await Dbs.CreateAsync(actor, ct);
        var item = await db.ReadinessItems.FirstOrDefaultAsync(i => i.Id == itemId, ct) ?? throw new NotFoundException();
        var app = await LoadCaseAsync(db, actor, item.ApplicationId, ct);
        await RequireAssignedAsync(db, actor, app, ct);
        ExpectVersion(db, app, caseVersion);
        if (app.Status is not (CaseStatus.Permitted or CaseStatus.CheckedIn))
            throw new BusinessRuleException(ErrorCodes.InvalidTransition, "The readiness checklist is completed on the event day (Permitted or Checked In).");
        item.Done = done;
        item.DoneBy = done ? actor.DisplayName : null;
        item.DoneAt = done ? Now : null;
        Touch(db, app);
        AuditLog(db, actor, "Readiness.Changed", nameof(ReadinessChecklistItem), item.Id, $"{(done ? "Done" : "Not done")}: {item.Label}", app.Id);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>T11: token valid and inside the window; assigned attendant only; idempotent (a repeat returns the existing record, E8-02 AC2).</summary>
    public async Task<CaseRef> CheckInAsync(Actor actor, string? code, CancellationToken ct = default)
    {
        actor.Require(Cap.RecordCheckIn);
        var check = await permits.VerifyAsync(actor, code, ct);
        if (check.ApplicationId is not { } appId) throw new BusinessRuleException(ErrorCodes.PermitInvalid, check.Message);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, appId, ct, q => q.Include(a => a.Permit!).ThenInclude(p => p.Versions));
        await RequireAssignedAsync(db, actor, app, ct);
        var current = app.Permit!.Versions.First(v => v.VersionNumber == app.Permit.CurrentVersion);
        var existing = await db.CheckIns.FirstOrDefaultAsync(c => c.ApplicationId == app.Id && c.PermitVersionId == current.Id, ct);
        if (existing is not null && app.Status != CaseStatus.Permitted)
            return new CaseRef(app.Id, app.Reference, app.Status, app.Version); // idempotent repeat
        if (check.Result != TokenResult.Valid) throw new BusinessRuleException(ErrorCodes.PermitInvalid, check.Message);
        RequireTransition(app, Transition.CheckIn);
        db.CheckIns.Add(new CheckIn { ApplicationId = app.Id, PermitVersionId = current.Id, AttendantUserId = actor.UserId, AttendantName = actor.DisplayName, CheckedInAt = Now });
        ApplyTransition(db, actor, app, Transition.CheckIn, $"Permit {app.Permit.Number} v{current.VersionNumber} verified");
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>Records the departure (idempotent: a second check-out keeps the first time).</summary>
    public async Task<CaseRef> CheckOutAsync(Actor actor, Guid applicationId, uint? version, string? notes, CancellationToken ct = default)
    {
        actor.Require(Cap.RecordCheckIn);
        new Validator().Text("notes", notes, 0, 1000, "Notes", false).ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, applicationId, ct);
        await RequireAssignedAsync(db, actor, app, ct);
        if (app.Status != CaseStatus.CheckedIn) throw new BusinessRuleException(ErrorCodes.InvalidTransition, "Check-out is recorded after check-in, before the event is completed.");
        var checkIn = await db.CheckIns.OrderByDescending(c => c.CheckedInAt).FirstAsync(c => c.ApplicationId == app.Id, ct);
        if (checkIn.CheckedOutAt is not null) return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
        ExpectVersion(db, app, version);
        checkIn.CheckedOutAt = Now;
        checkIn.Notes = notes?.Trim() ?? "";
        Touch(db, app);
        AuditLog(db, actor, "CheckIn.CheckedOut", nameof(CheckIn), checkIn.Id, "Departure recorded", app.Id);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>T12: check-out recorded and the readiness checklist complete.</summary>
    public async Task<CaseRef> CompleteAsync(Actor actor, Guid applicationId, uint? version, CancellationToken ct = default)
    {
        actor.Require(Cap.RecordCheckIn);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, applicationId, ct, q => q.Include(a => a.ReadinessItems));
        await RequireAssignedAsync(db, actor, app, ct);
        ExpectVersion(db, app, version);
        RequireTransition(app, Transition.Complete);
        var checkIn = await db.CheckIns.AsNoTracking().OrderByDescending(c => c.CheckedInAt).FirstOrDefaultAsync(c => c.ApplicationId == app.Id, ct);
        if (checkIn?.CheckedOutAt is null) throw new BusinessRuleException(ErrorCodes.CheckOutMissing, "Record the check-out before completing the event.");
        var open = app.ReadinessItems.Count(i => !i.Done);
        if (open > 0) throw new BusinessRuleException(ErrorCodes.ReadinessIncomplete, $"Finish the readiness checklist first ({open} item(s) open).");
        ApplyTransition(db, actor, app, Transition.Complete);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>T13: unresolved High/Critical incidents block closure (brief §5).</summary>
    public async Task<CaseRef> CloseAsync(Actor actor, Guid applicationId, uint? version, string? note, CancellationToken ct = default)
    {
        actor.Require(Cap.CloseCases);
        new Validator().Text("note", note, 0, Limits.ReasonMax, "Closing note", false).ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, applicationId, ct, q => q.Include(a => a.Incidents));
        ExpectVersion(db, app, version);
        RequireTransition(app, Transition.Close);
        var blocking = app.Incidents.Where(i => i.BlocksClosure).ToList();
        if (blocking.Count > 0)
            throw new BusinessRuleException(ErrorCodes.BlockingIncidents, $"Resolve these High/Critical incidents first: {string.Join(", ", blocking.Select(i => $"{i.Reference} ({i.Severity})"))}.");
        ApplyTransition(db, actor, app, Transition.Close, note?.Trim());
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>T14: reason recorded; reservations and allocations released; permit invalidated; external hold released.</summary>
    public async Task<CaseRef> CancelAsync(Actor actor, Guid applicationId, uint? version, string? reason, CancellationToken ct = default)
    {
        actor.Require(Cap.CancelCases);
        new Validator().Text("reason", reason, Limits.ReasonMin, Limits.ReasonMax, "Cancellation reason").ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, applicationId, ct, q => q.Include(a => a.Reservations).ThenInclude(r => r.Allocations)
            .Include(a => a.Permit!).ThenInclude(p => p.Versions).Include(a => a.Applicant).Include(a => a.Event));
        ExpectVersion(db, app, version);
        RequireTransition(app, Transition.Cancel);
        SchedulingService.ReleaseAll(db, app, "Case cancelled", Correlation, Now);
        if (app.Permit is { } permit)
        {
            permit.Status = PermitStatus.Cancelled;
            foreach (var v in permit.Versions.Where(v => v.Status == PermitVersionStatus.Active)) v.Status = PermitVersionStatus.Cancelled;
        }
        app.DecisionReason = reason!.Trim();
        ApplyTransition(db, actor, app, Transition.Cancel, reason.Trim());
        await NotificationOutbox.QueueAsync(db, Correlation, app, "case.cancelled", new Dictionary<string, string> { ["Reason"] = reason.Trim() }, ct: ct);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>Printable run-of-show for a date and cemetery (E8-01 AC4).</summary>
    public async Task<byte[]> RunOfShowAsync(Actor actor, DateOnly date, Guid? cemeteryId, CancellationToken ct = default)
    {
        var rows = await RosterAsync(actor, date, cemeteryId, ct);
        var table = new List<string[]> { new[] { "Time", "Venue", "Event", "Applicant", "Permit", "Attendees", "Attendants" } };
        table.AddRange(rows.Select(r => new[]
        {
            $"{TimeZoneInfo.ConvertTime(r.StartUtc, CaseService.Zone(r.TimeZoneId)):HH:mm}–{TimeZoneInfo.ConvertTime(r.EndUtc, CaseService.Zone(r.TimeZoneId)):HH:mm}",
            r.VenueName, $"{r.Reference} {r.Title}", r.ApplicantName, r.PermitNumber ?? "—", r.Attendees.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(", ", r.Attendants.Select(a => a.UserName)),
        }));
        return pdf.RenderRunOfShow($"Run of show — {date:dddd d MMMM yyyy}", table, $"{rows.Count} event(s). Generated {Now:yyyy-MM-dd HH:mm} UTC by {actor.DisplayName}.");
    }

    private static async Task RequireAssignedAsync(AppDbContext db, Actor actor, PermitApplication app, CancellationToken ct)
    {
        if (!await db.AttendantAssignments.AnyAsync(a => a.ApplicationId == app.Id && a.UserId == actor.UserId && a.Active, ct))
            throw new ForbiddenException("Only the attendant assigned to this event can record this.", ErrorCodes.NotAssignedAttendant);
    }
}

/// <summary>Incident register (FR-11, E8-03).</summary>
public sealed partial class IncidentService(AppDb dbs, ICorrelationContext correlation, FileService files) : ServiceBase(dbs, correlation)
{
    private static readonly CaseStatus[] IncidentStatuses = [CaseStatus.Permitted, CaseStatus.CheckedIn, CaseStatus.EventCompleted];

    public async Task<(IReadOnlyList<IncidentRow> Rows, int Total)> ListAsync(Actor actor, IncidentFilter filter, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewIncidents);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var visible = Scoped(db, actor).Select(a => a.Id);
        var q = from i in db.Incidents.AsNoTracking()
                join a in db.Applications.AsNoTracking() on i.ApplicationId equals a.Id
                where visible.Contains(a.Id)
                select new { i, a.Reference, a.Event!.Title, a.Event.CemeteryId, Photos = db.Attachments.Count(x => x.IncidentId == i.Id) };
        if (filter.Severity is { } s) q = q.Where(x => x.i.Severity == s);
        if (filter.Status is { } st) q = q.Where(x => x.i.Status == st);
        if (filter.CemeteryId is { } c) q = q.Where(x => x.CemeteryId == c);
        if (filter.From is { } from) q = q.Where(x => x.i.CreatedAt >= new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        if (filter.To is { } to) q = q.Where(x => x.i.CreatedAt < new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(x => x.i.Status == IncidentStatus.Resolved).ThenByDescending(x => x.i.Severity).ThenByDescending(x => x.i.CreatedAt)
            .Skip((Math.Max(1, filter.Page) - 1) * Limits.PageSize).Take(Limits.PageSize).ToListAsync(ct);
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        return (rows.Select(x => new IncidentRow(x.i.Id, x.i.Reference, x.i.ApplicationId, x.Reference, x.Title, x.i.Category, x.i.Severity, x.i.Status, x.i.Description,
            x.i.ReportedByName, x.i.OwnerName, x.i.DueDate, x.i.Status != IncidentStatus.Resolved && (x.i.IsOverdue || x.i.DueDate < today), x.i.CreatedAt, x.Photos, x.i.Version)).ToList(), total);
    }

    public async Task<(Incident Incident, IReadOnlyList<AttachmentView> Photos, IReadOnlyList<AuditEvent> History, string CaseReference)> GetAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewIncidents);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var incident = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException();
        var app = await Scoped(db, actor).AsNoTracking().FirstOrDefaultAsync(a => a.Id == incident.ApplicationId, ct) ?? throw new NotFoundException();
        var photos = await db.Attachments.AsNoTracking().Where(a => a.IncidentId == id).OrderBy(a => a.UploadedAt).ToListAsync(ct);
        var history = await db.AuditEvents.AsNoTracking().Where(a => a.EntityType == nameof(Incident) && a.EntityId == id.ToString()).OrderByDescending(a => a.At).ToListAsync(ct);
        return (incident, photos.Select(p => FileService.View(p, app.Reference, FileService.CanDownload(actor))).ToList(), history, app.Reference);
    }

    public async Task<Guid> CreateAsync(Actor actor, Guid applicationId, uint? caseVersion, IncidentInput input, CancellationToken ct = default)
    {
        actor.Require(Cap.ReportIncidents);
        Validate(input, requireOwner: false);
        await using var db = await Dbs.CreateAsync(actor, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var app = await LoadCaseAsync(db, actor, applicationId, ct);
        if (actor.Has(Roles.EventAttendant) && !actor.Has(Roles.OperationsManager) &&
            !await db.AttendantAssignments.AnyAsync(a => a.ApplicationId == app.Id && a.UserId == actor.UserId && a.Active, ct))
            throw new ForbiddenException("Only the attendant assigned to this event can log incidents for it.", ErrorCodes.NotAssignedAttendant);
        ExpectVersion(db, app, caseVersion);
        if (!IncidentStatuses.Contains(app.Status))
            throw new BusinessRuleException(ErrorCodes.InvalidTransition, $"Incidents are logged for permitted events (the case is {Workflow.Display(app.Status)}).");
        var owner = await OwnerAsync(db, input.OwnerUserId, ct);
        var incident = new Incident
        {
            ApplicationId = app.Id,
            Reference = await ReferenceGenerator.NextAsync(db, Limits.IncidentPrefix, Now.Year, ct),
            Category = input.Category!.Trim(), Severity = input.Severity, Description = input.Description!.Trim(),
            ReportedBy = actor.UserId, ReportedByName = actor.DisplayName, OwnerUserId = owner?.Id, OwnerName = owner?.DisplayName,
            DueDate = input.DueDate, CorrectiveAction = input.CorrectiveAction?.Trim() ?? "",
        };
        db.Incidents.Add(incident);
        Touch(db, app);
        AuditLog(db, actor, "Incident.Created", nameof(Incident), incident.Id, $"{incident.Reference} {incident.Severity}: {incident.Category}", app.Id);
        await SaveAsync(db, ct);
        await tx.CommitAsync(ct);
        return incident.Id;
    }

    public async Task UpdateAsync(Actor actor, Guid id, uint? version, IncidentInput input, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageIncidents);
        Validate(input, requireOwner: false);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var incident = await LoadIncidentAsync(db, actor, id, ct);
        ExpectVersion(db, incident, version);
        if (incident.Status == IncidentStatus.Resolved) throw new BusinessRuleException(ErrorCodes.CaseClosed, "Resolved incidents are kept as they were.");
        var owner = await OwnerAsync(db, input.OwnerUserId, ct);
        var before = new { incident.Severity, incident.Category, incident.OwnerName, incident.DueDate, incident.CorrectiveAction };
        incident.Category = input.Category!.Trim();
        incident.Severity = input.Severity;
        incident.Description = input.Description!.Trim();
        incident.OwnerUserId = owner?.Id;
        incident.OwnerName = owner?.DisplayName;
        incident.DueDate = input.DueDate;
        incident.CorrectiveAction = input.CorrectiveAction?.Trim() ?? "";
        AuditLog(db, actor, "Incident.Updated", nameof(Incident), incident.Id, $"{incident.Reference} updated", incident.ApplicationId, before,
            new { incident.Severity, incident.Category, incident.OwnerName, incident.DueDate, incident.CorrectiveAction });
        await SaveAsync(db, ct);
    }

    public async Task EscalateAsync(Actor actor, Guid id, uint? version, string? reason, CancellationToken ct = default)
    {
        actor.Require(Cap.ReportIncidents);
        new Validator().Text("reason", reason, Limits.ReasonMin, Limits.ReasonMax, "Escalation reason").ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var incident = await LoadIncidentAsync(db, actor, id, ct);
        ExpectVersion(db, incident, version);
        if (incident.Status != IncidentStatus.Open) throw new BusinessRuleException(ErrorCodes.InvalidTransition, $"The incident is already {incident.Status}.");
        incident.Status = IncidentStatus.Escalated;
        incident.EscalatedAt = Now;
        var app = await db.Applications.Include(a => a.Applicant).Include(a => a.Event).FirstAsync(a => a.Id == incident.ApplicationId, ct);
        AuditLog(db, actor, "Incident.Escalated", nameof(Incident), incident.Id, $"{incident.Reference} escalated to operations management", incident.ApplicationId, reason: reason!.Trim());
        await NotificationOutbox.QueueAsync(db, Correlation, app, "incident.escalated", new Dictionary<string, string> { ["Reason"] = $"{incident.Reference} ({incident.Severity}): {reason.Trim()}" }, ct: ct);
        await SaveAsync(db, ct);
    }

    public async Task ResolveAsync(Actor actor, Guid id, uint? version, string? resolution, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageIncidents);
        new Validator().Text("resolution", resolution, Limits.ReasonMin, Limits.ReasonMax, "Resolution").ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var incident = await LoadIncidentAsync(db, actor, id, ct);
        ExpectVersion(db, incident, version);
        if (incident.Status == IncidentStatus.Resolved) throw new BusinessRuleException(ErrorCodes.InvalidTransition, "The incident is already resolved.");
        incident.Status = IncidentStatus.Resolved;
        incident.Resolution = resolution!.Trim();
        incident.ResolvedBy = actor.DisplayName;
        incident.ResolvedAt = Now;
        incident.IsOverdue = false;
        var app = await db.Applications.FirstAsync(a => a.Id == incident.ApplicationId, ct);
        Touch(db, app);
        AuditLog(db, actor, "Incident.Resolved", nameof(Incident), incident.Id, $"{incident.Reference} resolved", incident.ApplicationId, reason: resolution.Trim());
        await SaveAsync(db, ct);
    }

    public async Task AddPhotoAsync(Actor actor, Guid id, uint? version, UploadFile file, CancellationToken ct = default)
    {
        actor.Require(Cap.ReportIncidents);
        Guid applicationId;
        await using (var check = await Dbs.CreateAsync(actor, ct))
        {
            var current = await LoadIncidentAsync(check, actor, id, ct);
            if (current.Version != version) throw new ConflictException(null, current.Version);
            applicationId = current.ApplicationId;
        }
        var attachment = await files.StoreAsync(actor, file, AttachmentPurpose.IncidentPhoto, DocumentType.Other, imagesOnly: true, ct);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var incident = await LoadIncidentAsync(db, actor, id, ct);
        ExpectVersion(db, incident, version);
        attachment.ApplicationId = applicationId;
        attachment.IncidentId = incident.Id;
        db.Attachments.Add(attachment);
        incident.UpdatedAt = Now;
        db.Entry(incident).Property(i => i.UpdatedAt).IsModified = true;
        AuditLog(db, actor, "Attachment.Uploaded", nameof(Incident), incident.Id, $"Photo {attachment.FileName} added to {incident.Reference}", applicationId);
        await SaveAsync(db, ct);
    }

    /// <summary>Worker: flags unresolved incidents past their due date.</summary>
    public async Task<int> MarkOverdueAsync(CancellationToken ct)
    {
        await using var db = await Dbs.CreateAsync(ct);
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        var due = await db.Incidents.Where(i => i.Status != IncidentStatus.Resolved && !i.IsOverdue && i.DueDate != null && i.DueDate < today).ToListAsync(ct);
        foreach (var i in due) i.IsOverdue = true;
        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    private async Task<Incident> LoadIncidentAsync(AppDbContext db, Actor actor, Guid id, CancellationToken ct)
    {
        var incident = await db.Incidents.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException();
        if (!await Scoped(db, actor).AnyAsync(a => a.Id == incident.ApplicationId, ct)) throw new NotFoundException();
        return incident;
    }

    private static async Task<AppUser?> OwnerAsync(AppDbContext db, string? userId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(userId)) return null;
        var isManager = await (from u in db.Users join ur in db.UserRoles on u.Id equals ur.UserId join r in db.Roles on ur.RoleId equals r.Id
                               where u.Id == userId && u.IsActive && r.Name == Roles.OperationsManager select u).FirstOrDefaultAsync(ct);
        return isManager ?? throw ValidationException.For("ownerUserId", "The follow-up owner must be an active Operations Manager.");
    }

    private static void Validate(IncidentInput input, bool requireOwner) => new Validator()
        .Text("category", input.Category, 3, 80, "Category")
        .Text("description", input.Description, 10, 2000, "Description")
        .Text("correctiveAction", input.CorrectiveAction, 0, 2000, "Corrective action", false)
        .Require(!requireOwner || !string.IsNullOrEmpty(input.OwnerUserId), "ownerUserId", "Choose a follow-up owner.")
        .ThrowIfInvalid();
}
