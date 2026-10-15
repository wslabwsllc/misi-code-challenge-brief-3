using System.Globalization;
using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public sealed record ReviewQueueRow(Guid Id, string Reference, string ApplicantName, string Title, string? Category, string? Cemetery, DateTimeOffset? StartUtc,
    string? TimeZoneId, DateTimeOffset? SubmittedAt, VerificationOutcome? Registry, RegistryEligibility? Eligibility, int OpenRequests);

public sealed record ReviewWorkspace(CaseDetail Case, IReadOnlyList<string> Checklist, IReadOnlyList<RegistryVerification> Verifications, bool OverrideUnknown, bool OverrideFlagged, DateOnly DefaultDueDate);

public sealed record VerificationView(Guid Id, Guid? ApplicationId, string? ApplicationReference, string Reference, VerificationOutcome Outcome, string? MatchedName,
    RegistryEligibility? Eligibility, DateTimeOffset? VerifiedAt, DateTimeOffset CreatedAt, string RequestedBy, string Detail, Guid? AttemptId, TimeSpan? RetryAfter);

public sealed record InboxRow(Guid Id, Guid ApplicationId, string Reference, string ApplicantName, IReadOnlyList<string> MissingItems, string Message, DateOnly DueDate,
    InformationRequestStatus Status, bool Overdue, string RequestedByName, DateTimeOffset RequestedAt, string? ResponseText, DateTimeOffset? RespondedAt, CaseStatus CaseStatus, uint CaseVersion);

public enum InboxView { Open, Answered, Overdue, All }

/// <summary>Eligibility review queue, registry verification, decisions and information requests (FR-04, E5-01..E5-03).</summary>
public sealed partial class ReviewService(AppDb dbs, ICorrelationContext correlation, IApplicantRegistry registry, RuleService rules, CaseService cases)
    : ServiceBase(dbs, correlation)
{
    public async Task<(IReadOnlyList<ReviewQueueRow> Rows, int Total)> QueueAsync(Actor actor, string? search, int page, CancellationToken ct = default)
    {
        actor.Require(Cap.ReviewEligibility);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var q = db.Applications.AsNoTracking().Where(a => a.Status == CaseStatus.Submitted);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            q = q.Where(a => a.Reference.ToLower().Contains(term) || a.Applicant!.FullName.ToLower().Contains(term) || a.Event!.Title.ToLower().Contains(term));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(a => a.SubmittedAt).ThenBy(a => a.Reference)
            .Skip((Math.Max(page, 1) - 1) * Limits.PageSize).Take(Limits.PageSize)
            .Select(a => new
            {
                a.Id, a.Reference, Name = a.Applicant!.FullName, a.Event!.Title, Category = a.Event.Category!.Name, Cemetery = a.Event.Cemetery!.Name,
                a.Event.StartUtc, Tz = a.Event.Cemetery.TimeZoneId, a.SubmittedAt,
                Verification = db.RegistryVerifications.Where(v => v.ApplicationId == a.Id).OrderByDescending(v => v.CreatedAt).FirstOrDefault(),
                Open = db.InformationRequests.Count(r => r.ApplicationId == a.Id && r.Status == InformationRequestStatus.Open),
            }).ToListAsync(ct);
        return (rows.Select(r => new ReviewQueueRow(r.Id, r.Reference, r.Name, r.Title, r.Category, r.Cemetery, r.StartUtc, r.Tz, r.SubmittedAt,
            r.Verification?.Outcome, r.Verification?.Eligibility, r.Open)).ToList(), total);
    }

    public async Task<ReviewWorkspace> WorkspaceAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        actor.Require(Cap.ReviewEligibility);
        var detail = await cases.GetAsync(actor, id, ct);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var checklist = detail.Event.CategoryId is { } cid
            ? (await db.EventCategories.AsNoTracking().FirstAsync(c => c.Id == cid, ct)).ReviewChecklist
            : ["Applicant identity matches the registry record"];
        var verifications = await db.RegistryVerifications.AsNoTracking().Where(v => v.ApplicationId == id).OrderByDescending(v => v.CreatedAt).ToListAsync(ct);
        return new ReviewWorkspace(detail, checklist, verifications,
            await rules.FlagAsync(RuleKeys.RegistryOverrideUnknown, ct), await rules.FlagAsync(RuleKeys.RegistryOverrideFlagged, ct),
            await DefaultDueDateAsync(ct));
    }

    /// <summary>
    /// Calls the registry (no database transaction held), stores the evidence and an audit event (brief §7.1).
    /// 429/outage → Pending with the reason; 404 → NotFound (not retried); success → Matched with eligibility.
    /// </summary>
    public async Task<VerificationView> VerifyCaseAsync(Actor actor, Guid applicationId, CancellationToken ct = default)
    {
        actor.Require(Cap.VerifyRegistry);
        string reference;
        await using (var read = await Dbs.CreateAsync(actor, ct))
        {
            var app = await LoadCaseAsync(read, actor, applicationId, ct, q => q.Include(a => a.Applicant));
            reference = app.Applicant!.RegistryReference;
        }
        if (string.IsNullOrWhiteSpace(reference))
            throw new BusinessRuleException(ErrorCodes.RegistryEvidenceMissing, "The application has no registry reference to verify.");
        return await VerifyAsync(actor, reference, applicationId, ct);
    }

    /// <summary>Ad-hoc lookup from page 20 (optionally linked to a case).</summary>
    public async Task<VerificationView> VerifyAsync(Actor actor, string reference, Guid? applicationId, CancellationToken ct = default)
    {
        actor.Require(Cap.VerifyRegistry);
        new Validator().Text("reference", reference, 3, 40, "Reference").ThrowIfInvalid();
        var normalised = reference.Trim().ToUpperInvariant();
        var result = await registry.LookupAsync(normalised, applicationId, ct);

        await using var db = await Dbs.CreateAsync(actor, ct);
        string? appReference = null;
        if (applicationId is { } aid)
        {
            var app = await LoadCaseAsync(db, actor, aid, ct);
            appReference = app.Reference;
            Touch(db, app); // new evidence bumps the case version
        }
        var verification = new RegistryVerification
        {
            ApplicationId = applicationId,
            Reference = normalised,
            RequestedBy = actor.DisplayName,
            AttemptId = result.AttemptId,
            Outcome = result.Status switch
            {
                ExternalCallStatus.Ok => VerificationOutcome.Matched,
                ExternalCallStatus.NotFound => VerificationOutcome.NotFound,
                _ => VerificationOutcome.Pending,
            },
            MatchedName = result.Value?.MatchedName,
            Eligibility = result.Value?.Eligibility,
            VerifiedAt = result.Value?.VerifiedAt,
            Detail = result.Status switch
            {
                ExternalCallStatus.Ok => $"Matched {result.Value!.MatchedName} ({result.Value.Eligibility}).",
                ExternalCallStatus.NotFound => "No registry record exists for this reference.",
                ExternalCallStatus.RateLimited => "Verification pending: the registry is rate limiting" + (result.RetryAfter is { } ra ? $" (retry after {ra.TotalSeconds:0} s)." : "."),
                _ => "Verification pending: " + result.Detail,
            },
        };
        db.RegistryVerifications.Add(verification);
        AuditLog(db, actor, "Registry.Verification", nameof(RegistryVerification), verification.Id,
            $"Registry check for {normalised}: {verification.Outcome}{(verification.Eligibility is { } e ? $" / {e}" : "")}", applicationId);
        await SaveAsync(db, ct);
        return View(verification, appReference, result.RetryAfter);
    }

    public async Task<(IReadOnlyList<VerificationView> Rows, int Total)> VerificationHistoryAsync(Actor actor, string? reference, int page, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewRegistry);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var q = db.RegistryVerifications.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(reference)) q = q.Where(v => v.Reference.Contains(reference.Trim().ToUpperInvariant()));
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(v => v.CreatedAt).Skip((Math.Max(page, 1) - 1) * Limits.PageSize).Take(Limits.PageSize)
            .Select(v => new { v, Ref = db.Applications.Where(a => a.Id == v.ApplicationId).Select(a => a.Reference).FirstOrDefault() }).ToListAsync(ct);
        return (rows.Select(r => View(r.v, r.Ref, null)).ToList(), total);
    }

    /// <summary>T4: checklist complete and registry evidence Valid, or an override the rules allow (A-10).</summary>
    public async Task<CaseRef> PassAsync(Actor actor, Guid id, uint? version, IReadOnlyDictionary<string, bool> checklist, string? overrideReason, CancellationToken ct = default)
    {
        actor.Require(Cap.ReviewEligibility);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, id, ct, q => q.Include(a => a.Event!).ThenInclude(e => e!.Category));
        var expected = app.Event?.Category?.ReviewChecklist ?? [];
        var v = new Validator();
        if (!string.IsNullOrWhiteSpace(overrideReason))
            v.Text("overrideReason", overrideReason, Limits.OverrideReasonMin, Limits.ReasonMax, "Override reason");
        v.ThrowIfInvalid();
        ExpectVersion(db, app, version);
        RequireTransition(app, Transition.MarkEligible);
        var missing = expected.Where(item => !checklist.TryGetValue(item, out var ok) || !ok).ToList();
        if (missing.Count > 0)
            throw new BusinessRuleException(ErrorCodes.ChecklistIncomplete, $"Resolve every checklist item first ({missing.Count} open: {string.Join("; ", missing)}).");

        var verification = await db.RegistryVerifications.Where(x => x.ApplicationId == app.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        var needsOverride = false;
        if (verification is null || verification.Outcome == VerificationOutcome.Pending)
            throw new BusinessRuleException(ErrorCodes.RegistryEvidenceMissing, "Eligibility cannot pass without registry evidence. Verify the applicant (or retry a pending verification) first.");
        if (verification.Outcome == VerificationOutcome.NotFound || verification.Eligibility == RegistryEligibility.Unknown)
        {
            needsOverride = true;
            if (!await rules.FlagAsync(RuleKeys.RegistryOverrideUnknown, ct))
                throw new BusinessRuleException(ErrorCodes.RegistryOverrideNotAllowed, "The registry did not confirm this applicant and the rules do not allow an override.");
        }
        else if (verification.Eligibility == RegistryEligibility.Flagged)
        {
            needsOverride = true;
            if (!await rules.FlagAsync(RuleKeys.RegistryOverrideFlagged, ct))
                throw new BusinessRuleException(ErrorCodes.RegistryOverrideNotAllowed, "The registry flagged this applicant. Flagged results cannot be overridden under the current rules; reject or request information instead.");
        }
        if (needsOverride && string.IsNullOrWhiteSpace(overrideReason))
            throw ValidationException.For("overrideReason", $"Give an override reason of at least {Limits.OverrideReasonMin} characters for the {(verification.Eligibility?.ToString() ?? verification.Outcome.ToString())} registry result.");

        db.EligibilityReviews.Add(new EligibilityReview
        {
            ApplicationId = app.Id,
            ReviewerUserId = actor.UserId,
            ReviewerName = actor.DisplayName,
            Decision = ReviewDecision.Pass,
            Checklist = checklist.ToDictionary(k => k.Key, k => k.Value),
            RegistryOverride = needsOverride,
            OverrideReason = needsOverride ? overrideReason!.Trim() : null,
            RegistryVerificationId = verification.Id,
        });
        if (needsOverride)
            AuditLog(db, actor, "Registry.Override", nameof(PermitApplication), app.Id, $"Registry result {verification.Eligibility?.ToString() ?? verification.Outcome.ToString()} overridden", app.Id, reason: overrideReason!.Trim());
        ApplyTransition(db, actor, app, Transition.MarkEligible);
        await NotificationOutbox.QueueAsync(db, Correlation, app, "eligibility.passed", ct: ct);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>T5: rejection needs a reason (brief §4).</summary>
    public async Task<CaseRef> RejectAsync(Actor actor, Guid id, uint? version, string? reason, IReadOnlyDictionary<string, bool>? checklist, CancellationToken ct = default)
    {
        actor.Require(Cap.ReviewEligibility);
        new Validator().Text("reason", reason, Limits.ReasonMin, Limits.ReasonMax, "Rejection reason").ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, id, ct);
        ExpectVersion(db, app, version);
        RequireTransition(app, Transition.RejectEligibility);
        db.EligibilityReviews.Add(new EligibilityReview
        {
            ApplicationId = app.Id, ReviewerUserId = actor.UserId, ReviewerName = actor.DisplayName, Decision = ReviewDecision.Reject,
            Checklist = checklist?.ToDictionary(k => k.Key, k => k.Value) ?? [], Reason = reason!.Trim(),
        });
        app.DecisionReason = reason.Trim();
        ApplyTransition(db, actor, app, Transition.RejectEligibility, reason.Trim());
        await NotificationOutbox.QueueAsync(db, Correlation, app, "eligibility.rejected", new Dictionary<string, string> { ["Reason"] = reason.Trim() }, ct: ct);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>T2: missing items and a future due date (A-13).</summary>
    public async Task<CaseRef> RequestInformationAsync(Actor actor, Guid id, uint? version, IReadOnlyList<string> items, string? message, DateOnly? dueDate, CancellationToken ct = default)
    {
        actor.Require(Cap.ReviewEligibility);
        var cleaned = items.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct().ToList();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        var due = dueDate ?? await DefaultDueDateAsync(ct);
        var v = new Validator()
            .Require(cleaned.Count > 0, "items", "List at least one missing item.")
            .Require(cleaned.All(i => i.Length <= 200), "items", "Each item must be at most 200 characters.")
            .Text("message", message, 0, Limits.ReasonMax, "Message", false)
            .Require(due > today, "dueDate", "The due date must be in the future.")
            .Require(due <= today.AddDays(90), "dueDate", "The due date must be within 90 days.");
        v.ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, id, ct);
        ExpectVersion(db, app, version);
        RequireTransition(app, Transition.RequestInformation);
        var request = new InformationRequest
        {
            ApplicationId = app.Id, RequestedBy = actor.UserId, RequestedByName = actor.DisplayName, MissingItems = cleaned,
            Message = message?.Trim() ?? "", DueDate = due,
        };
        db.InformationRequests.Add(request);
        db.EligibilityReviews.Add(new EligibilityReview
        {
            ApplicationId = app.Id, ReviewerUserId = actor.UserId, ReviewerName = actor.DisplayName, Decision = ReviewDecision.RequestInformation,
            Reason = string.Join("; ", cleaned),
        });
        ApplyTransition(db, actor, app, Transition.RequestInformation, $"Due {due:yyyy-MM-dd}: {string.Join("; ", cleaned)}");
        await NotificationOutbox.QueueAsync(db, Correlation, app, "info.requested", new Dictionary<string, string>
        {
            ["Items"] = string.Join("; ", cleaned),
            ["DueDate"] = due.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture),
        }, ct: ct);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    public async Task<(IReadOnlyList<InboxRow> Rows, int Total)> InboxAsync(Actor actor, InboxView view, int page, CancellationToken ct = default)
    {
        actor.Require(Cap.TrackInformationRequests);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        var visible = Scoped(db, actor).Select(a => a.Id);
        var q = db.InformationRequests.AsNoTracking().Where(r => visible.Contains(r.ApplicationId));
        q = view switch
        {
            InboxView.Open => q.Where(r => r.Status == InformationRequestStatus.Open),
            InboxView.Answered => q.Where(r => r.Status == InformationRequestStatus.Answered),
            InboxView.Overdue => q.Where(r => r.Status == InformationRequestStatus.Open && r.DueDate < today),
            _ => q,
        };
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(r => r.Status).ThenBy(r => r.DueDate).Skip((Math.Max(page, 1) - 1) * Limits.PageSize).Take(Limits.PageSize)
            .Join(db.Applications, r => r.ApplicationId, a => a.Id, (r, a) => new { r, a.Reference, Name = a.Applicant!.FullName, a.Status, a.Version })
            .ToListAsync(ct);
        return (rows.Select(x => new InboxRow(x.r.Id, x.r.ApplicationId, x.Reference, x.Name, x.r.MissingItems, x.r.Message, x.r.DueDate, x.r.Status,
            x.r.Status == InformationRequestStatus.Open && (x.r.IsOverdue || x.r.DueDate < today), x.r.RequestedByName, x.r.CreatedAt, x.r.ResponseText, x.r.RespondedAt, x.Status, x.Version)).ToList(), total);
    }

    /// <summary>The coordinator answers one request; earlier requests and answers stay immutable (E5-03 AC3).</summary>
    public async Task<CaseRef> RespondAsync(Actor actor, Guid requestId, uint? caseVersion, string? response, CancellationToken ct = default)
    {
        actor.Require(Cap.RespondToRequests);
        new Validator().Text("response", response, 5, 2000, "Response").ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var request = await db.InformationRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct) ?? throw new NotFoundException();
        var app = await LoadCaseAsync(db, actor, request.ApplicationId, ct);
        if (app.OwnerUserId != actor.UserId) throw new ForbiddenException("Only the coordinator who created this application can answer.", ErrorCodes.NotOwner);
        ExpectVersion(db, app, caseVersion);
        if (app.Status != CaseStatus.NeedsInformation)
            throw new BusinessRuleException(ErrorCodes.InvalidTransition, "Responses are possible only while the case needs information.");
        if (request.Status != InformationRequestStatus.Open)
            throw new BusinessRuleException(ErrorCodes.InvalidTransition, "This request has already been answered; answers are kept as they were.");
        request.Status = InformationRequestStatus.Answered;
        request.ResponseText = response!.Trim();
        request.RespondedAt = Now;
        request.RespondedBy = actor.DisplayName;
        request.IsOverdue = false;
        Touch(db, app);
        AuditLog(db, actor, "InformationRequest.Answered", nameof(InformationRequest), request.Id, $"Response to: {string.Join("; ", request.MissingItems)}", app.Id);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>T3: every open request answered.</summary>
    public async Task<CaseRef> ResubmitAsync(Actor actor, Guid id, uint? version, CancellationToken ct = default)
    {
        actor.Require(Cap.RespondToRequests);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, id, ct);
        if (app.OwnerUserId != actor.UserId) throw new ForbiddenException("Only the coordinator who created this application can resubmit it.", ErrorCodes.NotOwner);
        ExpectVersion(db, app, version);
        RequireTransition(app, Transition.Resubmit);
        var open = await db.InformationRequests.CountAsync(r => r.ApplicationId == app.Id && r.Status == InformationRequestStatus.Open, ct);
        if (open > 0) throw new BusinessRuleException(ErrorCodes.OpenRequestsUnanswered, $"Answer every open information request first ({open} open).");
        ApplyTransition(db, actor, app, Transition.Resubmit);
        await NotificationOutbox.QueueAsync(db, Correlation, app, "info.answered", ct: ct);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>Worker: flags open requests past their due date (not auto-rejected, A-13).</summary>
    public async Task<int> MarkOverdueAsync(CancellationToken ct)
    {
        await using var db = await Dbs.CreateAsync(ct);
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        var due = await db.InformationRequests.Where(r => r.Status == InformationRequestStatus.Open && !r.IsOverdue && r.DueDate < today).ToListAsync(ct);
        foreach (var r in due)
        {
            r.IsOverdue = true;
            Audit.Write(db, Actor.System, Correlation, "InformationRequest.Overdue", nameof(InformationRequest), r.Id, $"Response overdue since {r.DueDate:yyyy-MM-dd}", r.ApplicationId);
        }
        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    private async Task<DateOnly> DefaultDueDateAsync(CancellationToken ct)
    {
        var days = await rules.IntAsync(RuleKeys.InfoRequestDefaultBusinessDays, ct);
        var date = DateOnly.FromDateTime(Now.UtcDateTime);
        while (days > 0)
        {
            date = date.AddDays(1);
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days--;
        }
        return date;
    }

    private static VerificationView View(RegistryVerification v, string? appReference, TimeSpan? retryAfter) => new(v.Id, v.ApplicationId, appReference, v.Reference, v.Outcome,
        v.MatchedName, v.Eligibility, v.VerifiedAt, v.CreatedAt, v.RequestedBy, v.Detail, v.AttemptId, retryAfter);
}
