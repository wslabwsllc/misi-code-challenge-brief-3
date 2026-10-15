using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public sealed record ApprovalRow(Guid Id, string Reference, string Title, string Applicant, string? Venue, DateTimeOffset? StartUtc, string? TimeZoneId, int? Attendees,
    string? ReviewedBy, bool ReviewOverride, string? RegistryResult, bool CalendarReconciled, string? CalendarBlock, bool SevereWeather, bool WeatherAcknowledged,
    bool SeparationOfDuties, uint Version);

/// <summary>Approval Workspace (FR-08, E7-01): approve/reject with separation of duties and the calendar guard.</summary>
public sealed partial class ApprovalService(AppDb dbs, ICorrelationContext correlation) : ServiceBase(dbs, correlation)
{
    public async Task<IReadOnlyList<ApprovalRow>> QueueAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ApproveCases);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var apps = await db.Applications.AsNoTracking().Where(a => a.Status == CaseStatus.PendingApproval)
            .Include(a => a.Applicant).Include(a => a.Event!).ThenInclude(e => e!.Cemetery).Include(a => a.Event!).ThenInclude(e => e!.PreferredVenue)
            .OrderBy(a => a.Event!.StartUtc).ToListAsync(ct);
        var rows = new List<ApprovalRow>();
        foreach (var app in apps)
        {
            var facts = await CaseFacts.LoadAsync(db, app, Now, ct);
            var review = await db.EligibilityReviews.AsNoTracking().Where(r => r.ApplicationId == app.Id && r.Decision == ReviewDecision.Pass).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
            rows.Add(new ApprovalRow(app.Id, app.Reference, app.Event?.Title ?? "", app.Applicant?.FullName ?? "", app.Event?.PreferredVenue?.Name, app.Event?.StartUtc,
                app.Event?.Cemetery?.TimeZoneId, app.Event?.AttendeeCount, review?.ReviewerName, review?.RegistryOverride ?? false,
                facts.LatestVerification is { } v ? $"{v.Outcome}{(v.Eligibility is { } e ? $" / {e}" : "")}" : null,
                facts.CalendarReconciled, facts.CalendarBlock, facts.SevereWeather, facts.WeatherAcknowledged, facts.ReviewerIds.Contains(actor.UserId), app.Version));
        }
        return rows;
    }

    /// <summary>T8: the approver did not review the case (SoD, per user, A-03) and the regional calendar is reconciled (A-18/A-27).</summary>
    public async Task<CaseRef> ApproveAsync(Actor actor, Guid id, uint? version, string? notes, CancellationToken ct = default)
    {
        actor.Require(Cap.ApproveCases);
        new Validator().Text("notes", notes, 0, Limits.ReasonMax, "Decision notes", false).ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, id, ct, q => q.Include(a => a.Reservations));
        await RequireSeparationAsync(db, actor, app, ct);
        ExpectVersion(db, app, version);
        RequireTransition(app, Transition.Approve);
        var reservation = app.Reservations.FirstOrDefault(r => r.Status != ReservationStatus.Released)
                          ?? throw new BusinessRuleException(ErrorCodes.ReservationIncomplete, "The case has no reservation.");
        var (reconciled, block) = await CaseFacts.CalendarStateAsync(db, reservation, ct);
        if (!reconciled) throw new BusinessRuleException(ErrorCodes.CalendarNotReconciled, "Final approval is blocked: " + block);
        reservation.Status = ReservationStatus.Confirmed;
        db.ApprovalDecisions.Add(new ApprovalDecision
        {
            ApplicationId = app.Id, ApproverUserId = actor.UserId, ApproverName = actor.DisplayName, Outcome = ApprovalOutcome.Approved,
            Notes = notes?.Trim() ?? "", DecidedAt = Now,
        });
        ApplyTransition(db, actor, app, Transition.Approve, notes?.Trim());
        await NotificationOutbox.QueueAsync(db, Correlation, app, "approval.approved", ct: ct);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>T9: reason required; the reservation, allocations and calendar hold are released.</summary>
    public async Task<CaseRef> RejectAsync(Actor actor, Guid id, uint? version, string? reason, CancellationToken ct = default)
    {
        actor.Require(Cap.ApproveCases);
        new Validator().Text("reason", reason, Limits.ReasonMin, Limits.ReasonMax, "Rejection reason").ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, id, ct, q => q.Include(a => a.Reservations).ThenInclude(r => r.Allocations));
        await RequireSeparationAsync(db, actor, app, ct);
        ExpectVersion(db, app, version);
        RequireTransition(app, Transition.RejectApproval);
        db.ApprovalDecisions.Add(new ApprovalDecision
        {
            ApplicationId = app.Id, ApproverUserId = actor.UserId, ApproverName = actor.DisplayName, Outcome = ApprovalOutcome.Rejected,
            Notes = reason!.Trim(), DecidedAt = Now,
        });
        app.DecisionReason = reason.Trim();
        SchedulingService.ReleaseAll(db, app, "Rejected at approval", Correlation, Now);
        ApplyTransition(db, actor, app, Transition.RejectApproval, reason.Trim());
        await NotificationOutbox.QueueAsync(db, Correlation, app, "approval.rejected", new Dictionary<string, string> { ["Reason"] = reason.Trim() }, ct: ct);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>Severe weather must be acknowledged by a manager before issuance (A-19); never cancels the case.</summary>
    public async Task<CaseRef> AcknowledgeWeatherAsync(Actor actor, Guid id, uint? version, string? note, CancellationToken ct = default)
    {
        actor.Require(Cap.AcknowledgeWeather);
        new Validator().Text("note", note, Limits.ReasonMin, Limits.ReasonMax, "Acknowledgement note").ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, id, ct);
        ExpectVersion(db, app, version);
        if (Workflow.IsTerminal(app.Status)) throw new BusinessRuleException(ErrorCodes.CaseClosed, "The case is finished.");
        app.WeatherAcknowledgedAt = Now;
        app.WeatherAcknowledgedBy = actor.DisplayName;
        app.WeatherAcknowledgementNote = note!.Trim();
        AuditLog(db, actor, "Weather.Acknowledged", nameof(PermitApplication), app.Id, "Severe weather advisory acknowledged", app.Id, reason: note.Trim());
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    private static async Task RequireSeparationAsync(AppDbContext db, Actor actor, PermitApplication app, CancellationToken ct)
    {
        if (await db.EligibilityReviews.AnyAsync(r => r.ApplicationId == app.Id && r.ReviewerUserId == actor.UserId, ct))
            throw new ForbiddenException("Separation of duties: you recorded an eligibility review on this case, so you cannot make the final decision.", ErrorCodes.SeparationOfDuties);
    }
}
