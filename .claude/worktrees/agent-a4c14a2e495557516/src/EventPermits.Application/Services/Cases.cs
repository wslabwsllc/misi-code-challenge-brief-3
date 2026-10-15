using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public sealed record CaseRef(Guid Id, string Reference, CaseStatus Status, uint Version);

public sealed record ApplicantInput(string? FullName, string? Organization, string? Email, string? Phone, string? RegistryReference);

public sealed record EventInput(string? Title, Guid? CategoryId, Guid? VenueId, DateOnly? LocalDate, TimeOnly? LocalStart, int? DurationMinutes, int? AttendeeCount, string? Description);

public sealed record ResourceRequestInput(Guid ResourceId, int Quantity);

public sealed record FacilitiesInput(IReadOnlyList<ResourceRequestInput> Resources, string? SpecialNeeds);

public sealed record CategoryOption(Guid Id, string Code, string Name, string Description, IReadOnlyList<DocumentType> RequiredDocuments);
public sealed record VenueOption(Guid Id, string Code, string Name, Guid CemeteryId, string CemeteryName, string TimeZoneId, int Capacity, IReadOnlyList<string> Categories, bool IsActive);
public sealed record ResourceOption(Guid Id, Guid CemeteryId, string Code, string Name, string Unit, int Total);
public sealed record IntakeLookups(IReadOnlyList<CategoryOption> Categories, IReadOnlyList<VenueOption> Venues, IReadOnlyList<ResourceOption> Resources);

public sealed record ActionInfo(string Id, string Name, bool Enabled, string? BlockedReason);

public sealed record CaseDetail(
    Guid Id, string Reference, CaseStatus Status, uint Version, string OwnerUserId, string OwnerName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    int WizardStep, DateTimeOffset? SubmittedAt, string? DecisionReason,
    ApplicantView Applicant, EventView Event, IReadOnlyList<RequestedResourceView> RequestedResources,
    IReadOnlyList<AttachmentView> Documents, IReadOnlyList<Comment> Comments, IReadOnlyList<InformationRequest> InformationRequests,
    IReadOnlyList<EligibilityReview> Reviews, IReadOnlyList<ApprovalDecision> Approvals, ReservationView? Reservation, PermitView? Permit,
    IReadOnlyList<CheckIn> CheckIns, IReadOnlyList<Incident> Incidents, IReadOnlyList<AttendantAssignment> Assignments,
    IReadOnlyList<ReadinessChecklistItem> Readiness, IReadOnlyList<AuditEvent> Timeline, RegistryVerification? LatestVerification,
    IReadOnlyList<string> Flags, IReadOnlyList<ActionInfo> Actions, CaseEditRights Edit,
    DateTimeOffset? WeatherAcknowledgedAt, string? WeatherAcknowledgementNote, WeatherSnapshot? Weather);

public sealed record ApplicantView(string FullName, string Organization, string Email, string Phone, string RegistryReference, bool Masked);

public sealed record EventView(string Title, Guid? CategoryId, string? CategoryCode, string? CategoryName, Guid? CemeteryId, string? CemeteryName, string? TimeZoneId,
    Guid? VenueId, string? VenueName, int? VenueCapacity, DateTimeOffset? StartUtc, int? DurationMinutes, int? AttendeeCount, string Description, string SpecialNeeds,
    IReadOnlyList<DocumentType> RequiredDocuments);

public sealed record RequestedResourceView(Guid ResourceId, string Name, string Unit, int Quantity);

public sealed record ReservationView(Guid Id, Guid VenueId, string VenueName, DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset BlockStartUtc,
    DateTimeOffset BlockEndUtc, int AttendeeCount, ReservationStatus Status, string AttendantNeeds, CalendarPushState CalendarState, string? ExternalReservationId,
    IReadOnlyList<AllocationView> Allocations, IReadOnlyList<ReconciliationItem> Reconciliation);

public sealed record AllocationView(Guid Id, Guid ResourceId, string Name, string Unit, int Quantity, bool Released);

public sealed record PermitView(Guid Id, string Number, PermitStatus Status, int CurrentVersion, IReadOnlyList<PermitVersion> Versions, Guid? CurrentPdfAttachmentId);

public sealed record CaseEditRights(bool Applicant, bool Event, bool Facilities, bool Documents, bool Comment, bool Reservation);

/// <summary>Intake wizard, submission, comments and the case detail view (FR-02, E4-01, E4-03, E4-04).</summary>
public sealed partial class CaseService(AppDb dbs, ICorrelationContext correlation, FileService files, RuleService rules)
    : ServiceBase(dbs, correlation)
{
    public async Task<IntakeLookups> LookupsAsync(Actor actor, CancellationToken ct = default)
    {
        await using var db = await Dbs.CreateAsync(actor, ct);
        var categories = await db.EventCategories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name)
            .Select(c => new CategoryOption(c.Id, c.Code, c.Name, c.Description, c.RequiredDocuments)).ToListAsync(ct);
        var venues = await db.Venues.AsNoTracking().Include(v => v.Cemetery).OrderBy(v => v.Cemetery!.Name).ThenBy(v => v.Name)
            .Select(v => new VenueOption(v.Id, v.Code, v.Name, v.CemeteryId, v.Cemetery!.Name, v.Cemetery.TimeZoneId, v.Capacity, v.AllowedCategoryCodes, v.IsActive)).ToListAsync(ct);
        var resources = await db.Resources.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.Name)
            .Select(r => new ResourceOption(r.Id, r.CemeteryId, r.Code, r.Name, r.Unit, r.TotalQuantity)).ToListAsync(ct);
        return new IntakeLookups(categories, venues, resources);
    }

    /// <summary>Creates the draft once per client request key (E4-01 AC3): a double click or a retried POST returns the same draft.</summary>
    public async Task<CaseRef> CreateDraftAsync(Actor actor, string? clientRequestKey, CancellationToken ct = default)
    {
        actor.Require(Cap.CreateApplication);
        var key = string.IsNullOrWhiteSpace(clientRequestKey) ? null : $"{actor.UserId}:{clientRequestKey.Trim()}";
        if (key is { Length: > 200 }) throw ValidationException.For("clientRequestKey", "The request key is too long.");
        await using var db = await Dbs.CreateAsync(actor, ct);
        if (key is not null && await db.Applications.AsNoTracking().FirstOrDefaultAsync(a => a.ClientRequestKey == key, ct) is { } existing)
            return new CaseRef(existing.Id, existing.Reference, existing.Status, existing.Version);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var reference = await ReferenceGenerator.NextAsync(db, Limits.ApplicationPrefix, Now.Year, ct);
        var app = new PermitApplication
        {
            Reference = reference,
            OwnerUserId = actor.UserId,
            ClientRequestKey = key,
            Status = CaseStatus.Draft,
            StatusChangedAt = Now,
            Applicant = new Applicant(),
            Event = new EventDetails(),
        };
        db.Applications.Add(app);
        AuditLog(db, actor, "Application.Created", nameof(PermitApplication), app.Id, $"Draft {reference} created", app.Id);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException) when (key is not null)
        {
            // Two concurrent creates with the same key: the other one won; return it.
            await tx.RollbackAsync(ct);
            await using var again = await Dbs.CreateAsync(actor, ct);
            var winner = await again.Applications.AsNoTracking().FirstAsync(a => a.ClientRequestKey == key, ct);
            return new CaseRef(winner.Id, winner.Reference, winner.Status, winner.Version);
        }
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    public async Task<CaseRef> SaveApplicantAsync(Actor actor, Guid id, uint? version, ApplicantInput input, bool complete, CancellationToken ct = default)
    {
        actor.Require(Cap.CreateApplication);
        var v = new Validator()
            .Text("fullName", input.FullName, 2, Limits.ApplicantNameMax, "Applicant name", complete)
            .Text("organization", input.Organization, 0, 160, "Organisation", false)
            .Email("email", input.Email, "Email", false)
            .Text("phone", input.Phone, 7, 30, "Phone", false)
            .Text("registryReference", input.RegistryReference, 3, 40, "Registry reference", complete);
        if (complete && string.IsNullOrWhiteSpace(input.Email) && string.IsNullOrWhiteSpace(input.Phone))
            v.Add("email", "Give at least one contact: email or phone.");
        v.ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadOwnEditableAsync(db, actor, id, ct, q => q.Include(a => a.Applicant));
        ExpectVersion(db, app, version);
        var a = app.Applicant!;
        var before = new { a.FullName, a.Organization, Email = CaseScope.MaskEmail(a.Email), a.RegistryReference };
        a.FullName = input.FullName?.Trim() ?? "";
        a.Organization = input.Organization?.Trim() ?? "";
        a.Email = input.Email?.Trim() ?? "";
        a.Phone = input.Phone?.Trim() ?? "";
        a.RegistryReference = input.RegistryReference?.Trim().ToUpperInvariant() ?? "";
        app.WizardStep = Math.Max(app.WizardStep, complete ? 2 : 1);
        Touch(db, app);
        AuditLog(db, actor, "Application.ApplicantSaved", nameof(PermitApplication), app.Id, "Applicant details saved", app.Id, before,
            new { a.FullName, a.Organization, Email = CaseScope.MaskEmail(a.Email), a.RegistryReference });
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    public async Task<CaseRef> SaveEventAsync(Actor actor, Guid id, uint? version, EventInput input, bool complete, CancellationToken ct = default)
    {
        actor.Require(Cap.CreateApplication);
        var minDuration = await rules.IntAsync(RuleKeys.MinDurationMinutes, ct);
        var maxDuration = await rules.IntAsync(RuleKeys.MaxDurationMinutes, ct);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var v = new Validator()
            .Text("title", input.Title, Limits.TitleMin, Limits.TitleMax, "Event title", complete)
            .Text("description", input.Description, 0, Limits.DescriptionMax, "Description", false)
            .Require(!complete || input.CategoryId is not null, "categoryId", "Choose an event type.")
            .Require(!complete || input.VenueId is not null, "venueId", "Choose a venue.")
            .Require(!complete || input.LocalDate is not null, "localDate", "Choose the event date.")
            .Require(!complete || input.LocalStart is not null, "localStart", "Choose a start time.")
            .Require(input.DurationMinutes is null || (input.DurationMinutes >= minDuration && input.DurationMinutes <= maxDuration),
                "durationMinutes", $"Duration must be {minDuration} to {maxDuration} minutes.")
            .Require(!complete || input.DurationMinutes is not null, "durationMinutes", $"Enter a duration of {minDuration} to {maxDuration} minutes.")
            .Require(input.AttendeeCount is null || input.AttendeeCount >= 1, "attendeeCount", "At least one attendee is required.")
            .Require(!complete || input.AttendeeCount is not null, "attendeeCount", "Enter the expected number of attendees.");

        Venue? venue = null;
        if (input.VenueId is { } venueId)
        {
            venue = await db.Venues.Include(x => x.Cemetery).AsNoTracking().FirstOrDefaultAsync(x => x.Id == venueId && x.IsActive, ct);
            if (venue is null) v.Add("venueId", "Choose an active venue.");
            else if (input.AttendeeCount > venue.Capacity) v.Add("attendeeCount", $"{venue.Name} holds at most {venue.Capacity} attendees.");
        }
        EventCategory? category = null;
        if (input.CategoryId is { } categoryId)
        {
            category = await db.EventCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId && c.IsActive, ct);
            if (category is null) v.Add("categoryId", "Choose an active event type.");
            else if (venue is not null && venue.AllowedCategoryCodes.Count > 0 && !venue.AllowedCategoryCodes.Contains(category.Code))
                v.Add("venueId", $"{venue.Name} does not host {category.Name.ToLowerInvariant()} events.");
        }
        DateTimeOffset? startUtc = null;
        if (input.LocalDate is { } date && input.LocalStart is { } time && venue is not null)
        {
            startUtc = ToUtc(date, time, venue.Cemetery!.TimeZoneId);
            if (complete && startUtc <= Now) v.Add("localDate", "The event must be in the future.");
        }
        v.ThrowIfInvalid();

        var app = await LoadOwnEditableAsync(db, actor, id, ct, q => q.Include(a => a.Event));
        ExpectVersion(db, app, version);
        var e = app.Event!;
        var before = new { e.Title, e.CategoryId, e.PreferredVenueId, e.StartUtc, e.DurationMinutes, e.AttendeeCount };
        e.Title = input.Title?.Trim() ?? "";
        e.CategoryId = input.CategoryId;
        e.PreferredVenueId = input.VenueId;
        e.CemeteryId = venue?.CemeteryId;
        e.StartUtc = startUtc ?? (input.LocalDate is null ? null : e.StartUtc);
        e.DurationMinutes = input.DurationMinutes;
        e.AttendeeCount = input.AttendeeCount;
        e.Description = input.Description?.Trim() ?? "";
        app.WizardStep = Math.Max(app.WizardStep, complete ? 3 : 2);
        Touch(db, app);
        AuditLog(db, actor, "Application.EventSaved", nameof(PermitApplication), app.Id, "Event details saved", app.Id, before,
            new { e.Title, e.CategoryId, e.PreferredVenueId, e.StartUtc, e.DurationMinutes, e.AttendeeCount });
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    public async Task<CaseRef> SaveFacilitiesAsync(Actor actor, Guid id, uint? version, FacilitiesInput input, bool complete, CancellationToken ct = default)
    {
        actor.Require(Cap.CreateApplication);
        var v = new Validator().Text("specialNeeds", input.SpecialNeeds, 0, 1000, "Special needs", false);
        foreach (var r in input.Resources.Where(r => r.Quantity < 0 || r.Quantity > 1000))
            v.Add($"resource-{r.ResourceId}", "Quantity must be between 0 and 1000.");
        v.ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadOwnEditableAsync(db, actor, id, ct, q => q.Include(a => a.Event).Include(a => a.RequestedResources));
        ExpectVersion(db, app, version);
        var cemeteryId = app.Event!.CemeteryId;
        var resourceIds = input.Resources.Select(r => r.ResourceId).ToList();
        var known = await db.Resources.AsNoTracking().Where(r => resourceIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        foreach (var r in input.Resources)
        {
            if (!known.TryGetValue(r.ResourceId, out var resource)) v.Add($"resource-{r.ResourceId}", "Unknown resource.");
            else if (cemeteryId is not null && resource.CemeteryId != cemeteryId) v.Add($"resource-{r.ResourceId}", $"{resource.Name} is stocked at another cemetery.");
            else if (r.Quantity > resource.TotalQuantity) v.Add($"resource-{r.ResourceId}", $"Only {resource.TotalQuantity} {resource.Name.ToLowerInvariant()} exist at this cemetery.");
        }
        v.ThrowIfInvalid();
        foreach (var r in input.Resources)
        {
            var row = app.RequestedResources.FirstOrDefault(x => x.ResourceId == r.ResourceId);
            if (row is null && r.Quantity > 0) app.RequestedResources.Add(new RequestedResource { ResourceId = r.ResourceId, Quantity = r.Quantity });
            else if (row is not null) row.Quantity = r.Quantity; // zero means "not requested"; rows are never deleted
        }
        app.Event.SpecialNeeds = input.SpecialNeeds?.Trim() ?? "";
        app.WizardStep = Math.Max(app.WizardStep, complete ? 4 : 3);
        Touch(db, app);
        AuditLog(db, actor, "Application.FacilitiesSaved", nameof(PermitApplication), app.Id,
            $"Facilities saved ({input.Resources.Count(r => r.Quantity > 0)} resource types requested)", app.Id);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>Uploads a supporting document (Draft or Needs Information, own case only, A-26).</summary>
    public async Task<CaseRef> UploadDocumentAsync(Actor actor, Guid id, uint? version, DocumentType type, UploadFile file, Guid? informationRequestId = null, CancellationToken ct = default)
    {
        actor.Require(Cap.CreateApplication);
        await using (var check = await Dbs.CreateAsync(actor, ct))
        {
            var current = await LoadOwnEditableAsync(check, actor, id, ct);
            if (version is null) throw ValidationException.For("version", "The record version is required. Reload the page and try again.");
            if (current.Version != version) throw new ConflictException(null, current.Version);
        }
        // Store first (no database transaction is held during the storage call); an orphan is cleaned up if the save fails (E3-04 AC4).
        var attachment = await files.StoreAsync(actor, file, informationRequestId is null ? AttachmentPurpose.ApplicationDocument : AttachmentPurpose.InformationResponse, type, false, ct);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadOwnEditableAsync(db, actor, id, ct);
        ExpectVersion(db, app, version);
        if (informationRequestId is { } rid && !await db.InformationRequests.AnyAsync(r => r.Id == rid && r.ApplicationId == app.Id, ct))
            throw new NotFoundException();
        attachment.ApplicationId = app.Id;
        attachment.InformationRequestId = informationRequestId;
        db.Attachments.Add(attachment);
        Touch(db, app);
        AuditLog(db, actor, "Attachment.Uploaded", nameof(Attachment), attachment.Id,
            $"{attachment.FileName} uploaded as {type} ({attachment.SizeBytes:N0} bytes, sha256 {attachment.Sha256[..12]}…)", app.Id);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    /// <summary>T1. Validates the whole application; prevents duplicate submission through the version check.</summary>
    public async Task<CaseRef> SubmitAsync(Actor actor, Guid id, uint? version, CancellationToken ct = default)
    {
        actor.Require(Cap.CreateApplication);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, id, ct, Full);
        if (app.OwnerUserId != actor.UserId) throw new ForbiddenException("Only the coordinator who created this application can submit it.", ErrorCodes.NotOwner);
        ExpectVersion(db, app, version);
        RequireTransition(app, Transition.Submit);
        var problems = await SubmissionProblemsAsync(db, app, ct);
        if (problems.Count > 0)
            throw new BusinessRuleException(ErrorCodes.SubmissionIncomplete, "The application is not ready to submit: " + string.Join(" ", problems));
        app.SubmittedAt = Now;
        ApplyTransition(db, actor, app, Transition.Submit);
        await NotificationOutbox.QueueAsync(db, Correlation, app, "application.submitted", ct: ct);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    public async Task<CaseRef> AddCommentAsync(Actor actor, Guid id, uint? version, string? body, CancellationToken ct = default)
    {
        actor.Require(Cap.Comment);
        new Validator().Text("body", body, 1, Limits.CommentMax, "Comment").ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, id, ct);
        ExpectVersion(db, app, version);
        var comment = new Comment { ApplicationId = app.Id, AuthorUserId = actor.UserId, AuthorName = actor.DisplayName, Body = body!.Trim() };
        db.Comments.Add(comment);
        Touch(db, app);
        AuditLog(db, actor, "Comment.Added", nameof(Comment), comment.Id, $"Comment added by {actor.DisplayName}", app.Id);
        await SaveAsync(db, ct);
        return new CaseRef(app.Id, app.Reference, app.Status, app.Version);
    }

    public async Task<Guid?> FindAsync(Actor actor, string reference, CancellationToken ct = default)
    {
        await using var db = await Dbs.CreateAsync(actor, ct);
        var term = reference.Trim().ToUpperInvariant();
        return await Scoped(db, actor).Where(a => a.Reference == term).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
    }

    public async Task<CaseDetail> GetAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await Scoped(db, actor).AsNoTracking().AsSplitQuery()
            .Include(a => a.Applicant).Include(a => a.Event!).ThenInclude(e => e.Category)
            .Include(a => a.Event!).ThenInclude(e => e.Cemetery).Include(a => a.Event!).ThenInclude(e => e.PreferredVenue)
            .Include(a => a.RequestedResources).ThenInclude(r => r.Resource)
            .Include(a => a.Attachments).Include(a => a.Comments).Include(a => a.InformationRequests).Include(a => a.Reviews).Include(a => a.Approvals)
            .Include(a => a.Reservations).ThenInclude(r => r.Venue).Include(a => a.Reservations).ThenInclude(r => r.Allocations).ThenInclude(x => x.Resource)
            .Include(a => a.Permit!).ThenInclude(p => p.Versions)
            .Include(a => a.CheckIns).Include(a => a.Incidents).Include(a => a.Assignments).Include(a => a.ReadinessItems)
            .FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException();

        var owner = await db.Users.AsNoTracking().Where(u => u.Id == app.OwnerUserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "";
        var timeline = await db.AuditEvents.AsNoTracking().Where(e => e.ApplicationId == app.Id).OrderByDescending(e => e.At).Take(200).ToListAsync(ct);
        var verification = await db.RegistryVerifications.AsNoTracking().Where(v => v.ApplicationId == app.Id).OrderByDescending(v => v.CreatedAt).FirstOrDefaultAsync(ct);
        var reservation = app.Reservations.OrderByDescending(r => r.CreatedAt).FirstOrDefault(r => r.Status != ReservationStatus.Released)
                          ?? app.Reservations.OrderByDescending(r => r.CreatedAt).FirstOrDefault();
        var reconciliation = reservation is null ? [] : await db.ReconciliationItems.AsNoTracking().Where(r => r.ReservationId == reservation.Id).OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        var evtDate = app.Event?.StartUtc is { } s && app.Event.Cemetery is { } c ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s, Zone(c.TimeZoneId)).DateTime) : (DateOnly?)null;
        var weather = evtDate is { } d && app.Event?.PreferredVenueId is { } wv
            ? await db.WeatherSnapshots.AsNoTracking().Where(w => w.VenueId == (reservation != null ? reservation.VenueId : wv) && w.Date == d).OrderByDescending(w => w.RetrievedAt).FirstOrDefaultAsync(ct)
            : null;
        var facts = await CaseFacts.LoadAsync(db, app, Now, ct);

        var masked = CaseScope.MasksContacts(actor);
        var attendantOnly = actor.Has(Roles.EventAttendant) && !actor.Can(Cap.ViewRegister);
        var applicant = app.Applicant!;
        var applicantView = new ApplicantView(applicant.FullName, attendantOnly ? "" : applicant.Organization,
            masked || attendantOnly ? CaseScope.MaskEmail(applicant.Email) : applicant.Email,
            masked ? CaseScope.MaskPhone(applicant.Phone) : applicant.Phone,
            attendantOnly ? "" : applicant.RegistryReference, masked || attendantOnly);
        var e = app.Event!;
        var eventView = new EventView(e.Title, e.CategoryId, e.Category?.Code, e.Category?.Name, e.CemeteryId, e.Cemetery?.Name, e.Cemetery?.TimeZoneId,
            e.PreferredVenueId, e.PreferredVenue?.Name, e.PreferredVenue?.Capacity, e.StartUtc, e.DurationMinutes, e.AttendeeCount,
            attendantOnly ? "" : e.Description, e.SpecialNeeds, e.Category?.RequiredDocuments ?? []);
        var canDownload = FileService.CanDownload(actor);
        var documents = app.Attachments
            .Where(a => !attendantOnly || a.Purpose is AttachmentPurpose.PermitPdf or AttachmentPurpose.IncidentPhoto)
            .OrderByDescending(a => a.UploadedAt).Select(a => FileService.View(a, app.Reference, canDownload)).ToList();
        var permit = app.Permit is null ? null : new PermitView(app.Permit.Id, app.Permit.Number, app.Permit.Status, app.Permit.CurrentVersion,
            app.Permit.Versions.OrderByDescending(v => v.VersionNumber).ToList(),
            app.Attachments.Where(a => a.Purpose == AttachmentPurpose.PermitPdf && app.Permit.Versions.Any(v => v.PdfObjectKey == a.ObjectKey && v.VersionNumber == app.Permit.CurrentVersion)).Select(a => (Guid?)a.Id).FirstOrDefault());
        var reservationView = reservation is null ? null : new ReservationView(reservation.Id, reservation.VenueId, reservation.Venue?.Name ?? "", reservation.StartUtc, reservation.EndUtc,
            reservation.BlockStartUtc, reservation.BlockEndUtc, reservation.AttendeeCount, reservation.Status, reservation.AttendantNeeds, reservation.CalendarState, reservation.ExternalReservationId,
            reservation.Allocations.Select(x => new AllocationView(x.Id, x.ResourceId, x.Resource?.Name ?? "", x.Resource?.Unit ?? "", x.Quantity, x.Released)).ToList(), reconciliation);

        var own = app.OwnerUserId == actor.UserId;
        var editable = own && actor.Can(Cap.CreateApplication) && app.Status is CaseStatus.Draft or CaseStatus.NeedsInformation;
        var edit = new CaseEditRights(editable, editable, editable, editable,
            actor.Can(Cap.Comment) && !(actor.Has(Roles.IntakeCoordinator) && !own),
            actor.Can(Cap.ManageReservations) && app.Status is CaseStatus.Eligible or CaseStatus.TentativelyReserved);

        return new CaseDetail(app.Id, app.Reference, app.Status, app.Version, app.OwnerUserId, owner, app.CreatedAt, app.UpdatedAt, app.WizardStep, app.SubmittedAt, app.DecisionReason,
            applicantView, eventView,
            app.RequestedResources.Where(r => r.Quantity > 0).Select(r => new RequestedResourceView(r.ResourceId, r.Resource?.Name ?? "", r.Resource?.Unit ?? "", r.Quantity)).ToList(),
            documents, app.Comments.OrderByDescending(x => x.CreatedAt).ToList(), app.InformationRequests.OrderByDescending(x => x.CreatedAt).ToList(),
            attendantOnly ? [] : app.Reviews.OrderByDescending(x => x.CreatedAt).ToList(), app.Approvals.OrderByDescending(x => x.DecidedAt).ToList(),
            reservationView, permit, app.CheckIns.OrderByDescending(x => x.CheckedInAt).ToList(), app.Incidents.OrderByDescending(x => x.CreatedAt).ToList(),
            app.Assignments.Where(x => x.Active).ToList(), app.ReadinessItems.OrderBy(x => x.Order).ToList(),
            attendantOnly ? timeline.Where(t => t.Action.StartsWith("Transition", StringComparison.Ordinal) || t.Action.StartsWith("CheckIn", StringComparison.Ordinal) || t.Action.StartsWith("Incident", StringComparison.Ordinal)).ToList() : timeline,
            attendantOnly ? null : verification, facts.Flags(weather), facts.Actions(actor, app), edit,
            app.WeatherAcknowledgedAt, app.WeatherAcknowledgementNote, weather);
    }

    /// <summary>Everything that blocks T1, as human sentences (shown in the wizard before the user tries).</summary>
    public async Task<IReadOnlyList<string>> SubmissionProblemsAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        await using var db = await Dbs.CreateAsync(actor, ct);
        var app = await LoadCaseAsync(db, actor, id, ct, Full);
        return await SubmissionProblemsAsync(db, app, ct);
    }

    private async Task<List<string>> SubmissionProblemsAsync(AppDbContext db, PermitApplication app, CancellationToken ct)
    {
        var problems = new List<string>();
        var a = app.Applicant!;
        var e = app.Event!;
        if (string.IsNullOrWhiteSpace(a.FullName)) problems.Add("Applicant name is missing.");
        if (string.IsNullOrWhiteSpace(a.Email) && string.IsNullOrWhiteSpace(a.Phone)) problems.Add("Applicant contact (email or phone) is missing.");
        if (string.IsNullOrWhiteSpace(a.RegistryReference)) problems.Add("Registry reference is missing.");
        if (string.IsNullOrWhiteSpace(e.Title)) problems.Add("Event title is missing.");
        if (e.CategoryId is null) problems.Add("Event type is missing.");
        if (e.PreferredVenueId is null) problems.Add("Venue is missing.");
        if (e.StartUtc is null) problems.Add("Event date and time are missing.");
        else if (e.StartUtc <= Now) problems.Add("The event must be in the future.");
        var min = await rules.IntAsync(RuleKeys.MinDurationMinutes, ct);
        var max = await rules.IntAsync(RuleKeys.MaxDurationMinutes, ct);
        if (e.DurationMinutes is not { } d || d < min || d > max) problems.Add($"Duration must be {min} to {max} minutes.");
        if (e.AttendeeCount is not { } n || n < 1) problems.Add("Attendee count must be at least 1.");
        else if (e.PreferredVenueId is { } vid && await db.Venues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid, ct) is { } venue && n > venue.Capacity)
            problems.Add($"{n} attendees exceed the venue capacity of {venue.Capacity}.");
        if (e.CategoryId is { } cid && await db.EventCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) is { } category)
        {
            var missing = category.RequiredDocuments.Where(doc => !app.Attachments.Any(x => x.Purpose == AttachmentPurpose.ApplicationDocument && x.DocumentType == doc)).ToList();
            if (missing.Count > 0) problems.Add($"Required documents missing: {string.Join(", ", missing.Select(Label))}.");
        }
        if (!app.Attachments.Any(x => x.Purpose == AttachmentPurpose.ApplicationDocument)) problems.Add("At least one supporting document is required.");
        return problems;
    }

    public static string Label(DocumentType type) => type switch
    {
        DocumentType.ProofOfAffiliation => "Proof of affiliation",
        DocumentType.EventPlan => "Event plan",
        DocumentType.InsuranceCertificate => "Insurance certificate",
        DocumentType.SitePlan => "Site plan",
        _ => "Other document",
    };

    private static IQueryable<PermitApplication> Full(IQueryable<PermitApplication> q) =>
        q.Include(a => a.Applicant).Include(a => a.Event).Include(a => a.Attachments).Include(a => a.RequestedResources);

    /// <summary>Own case in an editable status (Draft or Needs Information, A-26).</summary>
    private static async Task<PermitApplication> LoadOwnEditableAsync(AppDbContext db, Actor actor, Guid id, CancellationToken ct,
        Func<IQueryable<PermitApplication>, IQueryable<PermitApplication>>? include = null)
    {
        var app = await LoadCaseAsync(db, actor, id, ct, include);
        if (app.OwnerUserId != actor.UserId) throw new ForbiddenException("Only the coordinator who created this application can change it.", ErrorCodes.NotOwner);
        if (app.Status is not (CaseStatus.Draft or CaseStatus.NeedsInformation))
            throw new BusinessRuleException(ErrorCodes.CaseClosed, $"The application cannot be edited while it is {Workflow.Display(app.Status)}.");
        return app;
    }

    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, string timeZoneId)
    {
        var zone = Zone(timeZoneId);
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    public static TimeZoneInfo Zone(string? id)
    {
        try { return string.IsNullOrEmpty(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }
}
