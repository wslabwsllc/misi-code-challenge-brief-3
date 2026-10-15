using System.Globalization;
using System.Text.Json;
using EventPermits.Application;
using EventPermits.Application.Services;
using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EventPermits.Infrastructure.Seeding;

public sealed partial class Seeder
{
    /// <summary>SystemState key holding the instant of the first case seed (A-29); every seeded date is relative to it.</summary>
    public const string AnchorKey = "Seed.AnchorUtc";

    /// <summary>Marker in <see cref="Entity.CreatedBy"/> for seeded rows that have no natural key of their own.</summary>
    public const string SeedMarker = "seed";

    public static bool CasesEnabled(IConfiguration config) => config.GetValue("Seed:Cases", false);

    /// <summary>
    /// E12-01: the brief §9 demo dataset. Planned in full from the stored anchor and a fixed random seed, then written
    /// case by case (one transaction each, keyed by <see cref="CaseSpec.Key"/>), so a re-run or a resumed run adds nothing twice.
    /// </summary>
    private async Task<(int Applications, int Files)> SeedCasesAsync(CancellationToken ct)
    {
        if (!CasesEnabled(Config)) return (0, 0);
        var world = await LoadWorldAsync(ct);
        var plan = new SeedPlanner(world).Build();
        await SeedEnvironmentAsync(world, plan, ct);
        var created = 0;
        var files = 0;
        foreach (var spec in plan.Cases.OrderBy(c => c.Created).ThenBy(c => c.No))
        {
            await using var db = await Factory.CreateDbContextAsync(ct);
            db.Clock = Clock;
            if (await db.Applications.AnyAsync(a => a.ClientRequestKey == spec.Key, ct)) continue;
            files += await new CaseWriter(this, world, spec, db, ct).WriteAsync();
            created++;
        }
        await SeedEvidenceAsync(world, plan, ct);
        return (created, files);
    }

    private async Task<SeedWorld> LoadWorldAsync(CancellationToken ct)
    {
        await using var db = await Factory.CreateDbContextAsync(ct);
        db.Clock = Clock;
        var state = await db.SystemStates.FirstOrDefaultAsync(s => s.Key == AnchorKey, ct);
        DateTimeOffset anchor;
        if (state is null)
        {
            anchor = SeedPlanner.Trim(Clock.GetUtcNow().ToUniversalTime());
            db.SystemStates.Add(new SystemState { Key = AnchorKey, Value = anchor.ToString("O", CultureInfo.InvariantCulture), UpdatedAt = anchor });
            await db.SaveChangesAsync(ct);
        }
        else anchor = DateTimeOffset.Parse(state.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

        var roleRows = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id select new { ur.UserId, r.Name }).ToListAsync(ct);
        var users = (await db.Users.AsNoTracking().ToListAsync(ct)).ToDictionary(u => u.UserName!,
            u => new SeedUser(u.Id, u.UserName!, u.DisplayName, roleRows.Where(r => r.UserId == u.Id).Select(r => r.Name!).ToHashSet()));
        var rules = await db.RuleSettings.AsNoTracking().ToDictionaryAsync(r => r.Key, r => r.Value, ct);
        int Rule(string key) => rules.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : int.Parse(RuleKeys.Defaults.First(d => d.Key == key).Value, CultureInfo.InvariantCulture);
        var dir = Config["Seed:ReferenceDirectory"];
        return new SeedWorld
        {
            Anchor = anchor,
            Users = users,
            Venues = await db.Venues.AsNoTracking().Include(v => v.Cemetery).Where(v => v.IsActive).OrderBy(v => v.Code).ToListAsync(ct),
            Categories = await db.EventCategories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Code).ToListAsync(ct),
            Resources = (await db.Resources.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.Code).ToListAsync(ct))
                .GroupBy(r => r.CemeteryId).ToDictionary(g => g.Key, g => g.ToList()),
            Templates = await db.NotificationTemplates.AsNoTracking().Where(t => t.IsActive).ToDictionaryAsync(t => t.Code, ct),
            Registry = ReferenceData.LoadRegistry(dir),
            Calendar = ReferenceData.LoadCalendar(dir),
            Weather = ReferenceData.LoadWeather(dir),
            CheckInOpensMinutes = Rule(RuleKeys.CheckInOpensMinutesBefore),
            TokenGraceHours = Rule(RuleKeys.TokenGraceHoursAfterEnd),
        };
    }

    public static string MessageId(int caseNo, string template) => $"msg-seed-{caseNo:D3}-{template.Replace('.', '-')}";

    /// <summary>
    /// Writes one case as the services would have, step by step: every status change goes through the transition table
    /// with the role that owns it and an audit event at the same instant, so the history replays to the final status.
    /// </summary>
    private sealed class CaseWriter(Seeder seeder, SeedWorld w, CaseSpec s, AppDbContext db, CancellationToken ct)
    {
        private static readonly JsonSerializerOptions Json = new();
        private const string OverrideText = "Registry returned Unknown; identity confirmed against a certified membership letter kept on file.";
        private const string WeatherNote = "Severe weather acknowledged; the organizer is briefed on the indoor contingency plan.";

        private PermitApplication _app = default!;
        private EventDetails _evt = default!;
        private Reservation? _reservation;
        private Permit? _permit;
        private RegistryVerification? _verification;
        private readonly List<InformationRequest> _requests = [];
        private readonly List<ReadinessChecklistItem> _readiness = [];
        private readonly HashSet<string> _notified = [];
        private DateTimeOffset _last;
        private int _files, _seq;

        public async Task<int> WriteAsync()
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.ActorId = s.Owner.Id;
            await IntakeAsync();
            var workflowEnd = s.Created;
            if (s.Path != SeedPath.Draft)
            {
                var steps = Steps();
                var first = s.Created.AddHours(2);
                var span = s.Limit - first;
                for (var i = 0; i < steps.Count; i++)
                {
                    var at = SeedPlanner.Trim(first + span * ((i + 1.0) / (steps.Count + 1)));
                    await steps[i](at);
                    workflowEnd = at;
                }
                await EventDayAsync();
            }
            Comments(workflowEnd);
            _app.UpdatedAt = _last;
            _app.UpdatedBy = _app.CreatedBy;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return _files;
        }

        // ------------------------------------------------------------------ intake (wizard, documents)

        private async Task IntakeAsync()
        {
            var c = s.Created;
            var reference = await ReferenceGenerator.NextAsync(db, Limits.ApplicationPrefix, c.Year, ct);
            var applicant = new Applicant
            {
                FullName = s.ApplicantName, Organization = s.Organization, Email = s.Email, Phone = s.Phone, RegistryReference = s.RegistryReference,
                CreatedAt = c, CreatedBy = s.Owner.Id,
            };
            _app = new PermitApplication
            {
                Reference = reference, Status = CaseStatus.Draft, OwnerUserId = s.Owner.Id, ApplicantId = applicant.Id, Applicant = applicant,
                ClientRequestKey = s.Key, WizardStep = 1, StatusChangedAt = c, CreatedAt = c, CreatedBy = s.Owner.Id,
            };
            _evt = new EventDetails { ApplicationId = _app.Id, CreatedAt = c, CreatedBy = s.Owner.Id };
            _app.Event = _evt;
            db.Applicants.Add(applicant);
            db.Applications.Add(_app);
            Log(s.Owner, c, "Application.Created", nameof(PermitApplication), _app.Id, $"Draft {reference} created");

            var t = c.AddMinutes(6);
            Log(s.Owner, t, "Application.ApplicantSaved", nameof(PermitApplication), _app.Id, "Applicant details saved");
            _app.WizardStep = 2;
            if (s.WizardSections >= 2)
            {
                t = t.AddMinutes(8);
                _evt.Title = s.Title;
                _evt.CategoryId = s.Category.Id;
                _evt.CemeteryId = s.Venue?.CemeteryId;
                _evt.PreferredVenueId = s.Venue?.Id;
                _evt.StartUtc = s.Start;
                _evt.DurationMinutes = s.Duration;
                _evt.AttendeeCount = s.Attendees;
                _evt.Description = $"{s.Category.Description} About {s.Attendees ?? 0} guests are expected.";
                Log(s.Owner, t, "Application.EventSaved", nameof(PermitApplication), _app.Id, "Event details saved");
                _app.WizardStep = 3;
            }
            if (s.WizardSections >= 3)
            {
                t = t.AddMinutes(6);
                _evt.SpecialNeeds = s.No % 3 == 0 ? "Step-free route and two reserved seats for guests using wheelchairs." : s.No % 3 == 1 ? "" : "Quiet area for an honor guard to assemble.";
                foreach (var (resource, quantity) in s.Resources)
                    db.RequestedResources.Add(new RequestedResource { ApplicationId = _app.Id, ResourceId = resource.Id, Quantity = quantity, CreatedAt = t, CreatedBy = s.Owner.Id });
                Log(s.Owner, t, "Application.FacilitiesSaved", nameof(PermitApplication), _app.Id, $"Facilities saved ({s.Resources.Count} resource types requested)");
                _app.WizardStep = 4;
            }
            if (s.WizardSections >= 4)
                foreach (var type in s.Category.RequiredDocuments)
                {
                    t = t.AddMinutes(3);
                    await DocumentAsync(type, t, AttachmentPurpose.ApplicationDocument, null);
                }
        }

        private async Task<Attachment> DocumentAsync(DocumentType type, DateTimeOffset at, AttachmentPurpose purpose, Guid? requestId)
        {
            var reference = _app.Reference;
            var label = CaseService.Label(type);
            var local = s.Start is { } st && s.Venue is { } v ? TimeZoneInfo.ConvertTime(st, SeedWorld.Zone(v)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "to be confirmed";
            byte[] PdfDoc() => SyntheticFiles.Pdf($"{label} - {reference}",
            [
                ("Application", reference), ("Applicant", s.ApplicantName), ("Organization", string.IsNullOrEmpty(s.Organization) ? "(private family)" : s.Organization),
                ("Event", s.Title), ("Venue", s.Venue?.Name ?? "to be confirmed"), ("Local start", local),
            ], type switch
            {
                DocumentType.EventPlan => "Programme: arrival and seating, opening words, readings, a moment of silence, closing music. Marshals are named on page two.",
                DocumentType.InsuranceCertificate => "Public liability cover for the event date and the venue named above, issued by a fictional insurer for practice use.",
                _ => "Response to the information request: the clarified details are confirmed above.",
            });
            var (name, bytes, contentType) = type switch
            {
                DocumentType.ProofOfAffiliation when s.No % 2 == 0 => ($"proof-of-affiliation-{reference}.png", SyntheticFiles.Png(48, 32, (32, 78, 89), (226, 232, 240)), "image/png"),
                DocumentType.ProofOfAffiliation => ($"proof-of-affiliation-{reference}.jpg", SyntheticFiles.Jpeg((byte)(150 + s.No % 80)), "image/jpeg"),
                DocumentType.SitePlan => ($"site-plan-{reference}.png", SyntheticFiles.Png(64, 48, (52, 120, 74), (240, 236, 220)), "image/png"),
                DocumentType.EventPlan => ($"event-plan-{reference}.pdf", PdfDoc(), "application/pdf"),
                DocumentType.InsuranceCertificate => ($"insurance-certificate-{reference}.pdf", PdfDoc(), "application/pdf"),
                _ => ($"information-response-{reference}.pdf", PdfDoc(), "application/pdf"),
            };
            var attachment = await StoreAsync(s.Owner, at, purpose, type, name, bytes, contentType);
            attachment.ApplicationId = _app.Id;
            attachment.InformationRequestId = requestId;
            Log(s.Owner, at, "Attachment.Uploaded", nameof(Attachment), attachment.Id,
                $"{attachment.FileName} uploaded as {type} ({attachment.SizeBytes:N0} bytes, sha256 {attachment.Sha256[..12]}…)");
            return attachment;
        }

        /// <summary>Same checks and key layout as FileService.StoreAsync; bytes go through the object store port.</summary>
        private async Task<Attachment> StoreAsync(SeedUser by, DateTimeOffset at, AttachmentPurpose purpose, DocumentType type, string name, byte[] bytes, string contentType)
        {
            var checkedFile = FileSignatures.Validate(name, contentType, bytes, Limits.MaxAttachmentBytes, FileSignatures.All);
            var key = $"{(purpose == AttachmentPurpose.PermitPdf ? "permits" : "attachments")}/{Guid.NewGuid():N}";
            await seeder.Store.PutAsync(key, bytes, checkedFile.ContentType, ct);
            _files++;
            var attachment = new Attachment
            {
                Purpose = purpose, DocumentType = type, FileName = checkedFile.SafeName, ContentType = checkedFile.ContentType, SizeBytes = checkedFile.Size,
                Sha256 = checkedFile.Sha256, ObjectKey = key, UploadedBy = by.Name, UploadedAt = at, CreatedAt = at, CreatedBy = by.Id,
            };
            db.Attachments.Add(attachment);
            return attachment;
        }

        // ------------------------------------------------------------------ workflow

        private List<Func<DateTimeOffset, Task>> Steps()
        {
            var p = s.Path;
            var list = new List<Func<DateTimeOffset, Task>> { SubmitAsync };
            if (s.Registry != RegistryPlan.None) list.Add(VerifyAsync);
            for (var i = 0; i < s.AnsweredRequests; i++)
            {
                list.Add(RequestInformationAsync);
                list.Add(AnswerAsync);
                list.Add(ResubmitAsync);
            }
            if (s.OpenRequest != RequestPlan.None) list.Add(RequestInformationAsync);
            if (p == SeedPath.RejectedEligibility) list.Add(RejectEligibilityAsync);
            if (p == SeedPath.CancelledSubmitted) list.Add(CancelAsync);
            if (SeedMix.PassesEligibility(p)) list.Add(PassAsync);
            if (p == SeedPath.CancelledEligible) list.Add(CancelAsync);
            if (SeedMix.HasReservation(p)) list.Add(ReserveAsync);
            if (p == SeedPath.CancelledReserved) list.Add(CancelAsync);
            if (SeedMix.ReachesApproval(p)) list.Add(SubmitForApprovalAsync);
            if (p == SeedPath.CancelledPending) list.Add(CancelAsync);
            if (p == SeedPath.RejectedApproval) list.Add(RejectApprovalAsync);
            if (SeedMix.Approved(p)) list.Add(ApproveAsync);
            if (s.Weather == WeatherPlan.SevereAcknowledged) list.Add(AcknowledgeWeatherAsync);
            if (s.AssignAttendant && !SeedMix.HasPermit(p)) list.Add(AssignAsync);
            if (p == SeedPath.CancelledApproved) list.Add(CancelAsync);
            if (SeedMix.HasPermit(p))
            {
                list.Add(IssueAsync);
                list.Add(AssignAsync);
            }
            if (s.Reissue) list.Add(ReissueAsync);
            if (s.Revoke) list.Add(RevokeAsync);
            if (p == SeedPath.CancelledPermitted) list.Add(CancelAsync);
            return list;
        }

        private Task SubmitAsync(DateTimeOffset at)
        {
            _app.SubmittedAt = at;
            Move(Transition.Submit, s.Owner, at);
            Notify("application.submitted", at);
            return Task.CompletedTask;
        }

        private Task VerifyAsync(DateTimeOffset at)
        {
            var (outcome, eligibility) = s.Registry switch
            {
                RegistryPlan.Valid => (VerificationOutcome.Matched, (RegistryEligibility?)RegistryEligibility.Valid),
                RegistryPlan.Flagged => (VerificationOutcome.Matched, RegistryEligibility.Flagged),
                RegistryPlan.Unknown => (VerificationOutcome.Matched, RegistryEligibility.Unknown),
                RegistryPlan.NotFound => (VerificationOutcome.NotFound, null),
                _ => (VerificationOutcome.Pending, null),
            };
            _verification = new RegistryVerification
            {
                ApplicationId = _app.Id, Reference = s.RegistryReference, Outcome = outcome, MatchedName = outcome == VerificationOutcome.Matched ? s.ApplicantName : null,
                Eligibility = eligibility, VerifiedAt = outcome == VerificationOutcome.Matched ? at : null, RequestedBy = s.Reviewer.Name, AttemptId = s.RegistryAttemptId,
                Detail = outcome switch
                {
                    VerificationOutcome.Matched => $"Matched {s.ApplicantName} ({eligibility}).",
                    VerificationOutcome.NotFound => "No registry record exists for this reference.",
                    _ => s.RegistryDetail ?? "Verification pending: the registry is unavailable.",
                },
                CreatedAt = at, CreatedBy = s.Reviewer.Id,
            };
            db.RegistryVerifications.Add(_verification);
            Log(s.Reviewer, at, "Registry.Verification", nameof(RegistryVerification), _verification.Id,
                $"Registry check for {s.RegistryReference}: {outcome}{(eligibility is { } e ? $" / {e}" : "")}");
            return Task.CompletedTask;
        }

        private static readonly string[] MissingItems =
        [
            "Proof of affiliation (a clearer copy)", "Updated attendee estimate", "Insurance certificate covering the event date",
            "Accessible parking requirements", "Name and phone of the on-site contact", "Programme with timings",
        ];

        private Task RequestInformationAsync(DateTimeOffset at)
        {
            var k = s.No + _requests.Count;
            var items = new List<string> { MissingItems[k % MissingItems.Length] };
            if (k % 3 == 0) items.Add(MissingItems[(k + 2) % MissingItems.Length]);
            var due = DateOnly.FromDateTime(at.UtcDateTime).AddDays(7);
            var request = new InformationRequest
            {
                ApplicationId = _app.Id, RequestedBy = s.Reviewer.Id, RequestedByName = s.Reviewer.Name, MissingItems = items,
                Message = "Please send the items below so the eligibility review can continue.", DueDate = due,
                IsOverdue = due < DateOnly.FromDateTime(w.Anchor.UtcDateTime), CreatedAt = at, CreatedBy = s.Reviewer.Id,
            };
            _requests.Add(request);
            db.InformationRequests.Add(request);
            db.EligibilityReviews.Add(new EligibilityReview
            {
                ApplicationId = _app.Id, ReviewerUserId = s.Reviewer.Id, ReviewerName = s.Reviewer.Name, Decision = ReviewDecision.RequestInformation,
                Reason = string.Join("; ", items), CreatedAt = at, CreatedBy = s.Reviewer.Id,
            });
            Move(Transition.RequestInformation, s.Reviewer, at, $"Due {due:yyyy-MM-dd}: {string.Join("; ", items)}");
            Notify("info.requested", at, new()
            {
                ["Items"] = string.Join("; ", items),
                ["DueDate"] = due.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture),
            });
            return Task.CompletedTask;
        }

        private async Task AnswerAsync(DateTimeOffset at)
        {
            var request = _requests[^1];
            request.Status = InformationRequestStatus.Answered;
            request.IsOverdue = false;
            request.ResponseText = "The requested details are attached and confirmed with the family.";
            request.RespondedBy = s.Owner.Name;
            request.RespondedAt = at;
            if (s.No % 2 == 0) await DocumentAsync(DocumentType.Other, at.AddMinutes(-2), AttachmentPurpose.InformationResponse, request.Id);
            Log(s.Owner, at, "InformationRequest.Answered", nameof(InformationRequest), request.Id, $"Response to: {string.Join("; ", request.MissingItems)}");
        }

        private Task ResubmitAsync(DateTimeOffset at)
        {
            Move(Transition.Resubmit, s.Owner, at);
            Notify("info.answered", at);
            return Task.CompletedTask;
        }

        private Task PassAsync(DateTimeOffset at)
        {
            if (_verification is null) throw new InvalidOperationException($"{s.Key}: a pass needs registry evidence.");
            db.EligibilityReviews.Add(new EligibilityReview
            {
                ApplicationId = _app.Id, ReviewerUserId = s.Reviewer.Id, ReviewerName = s.Reviewer.Name, Decision = ReviewDecision.Pass,
                Checklist = s.Category.ReviewChecklist.ToDictionary(x => x, _ => true), RegistryOverride = s.Override,
                OverrideReason = s.Override ? OverrideText : null, RegistryVerificationId = _verification.Id, CreatedAt = at, CreatedBy = s.Reviewer.Id,
            });
            if (s.Override)
                Log(s.Reviewer, at, "Registry.Override", nameof(PermitApplication), _app.Id, $"Registry result {_verification.Eligibility} overridden", reason: OverrideText);
            Move(Transition.MarkEligible, s.Reviewer, at);
            Notify("eligibility.passed", at);
            return Task.CompletedTask;
        }

        private Task RejectEligibilityAsync(DateTimeOffset at)
        {
            var checklist = s.Category.ReviewChecklist.Select((x, i) => (x, i)).ToDictionary(x => x.x, x => x.i != 1);
            db.EligibilityReviews.Add(new EligibilityReview
            {
                ApplicationId = _app.Id, ReviewerUserId = s.Reviewer.Id, ReviewerName = s.Reviewer.Name, Decision = ReviewDecision.Reject,
                Checklist = checklist, Reason = s.Reason, RegistryVerificationId = _verification?.Id, CreatedAt = at, CreatedBy = s.Reviewer.Id,
            });
            _app.DecisionReason = s.Reason;
            Move(Transition.RejectEligibility, s.Reviewer, at, s.Reason);
            Notify("eligibility.rejected", at, new() { ["Reason"] = s.Reason! });
            return Task.CompletedTask;
        }

        private Task ReserveAsync(DateTimeOffset at)
        {
            var venue = s.Venue!;
            var (blockStart, blockEnd) = ConflictEngine.Block(SeedTimeline.Facts(venue), s.Start!.Value, s.Duration!.Value);
            _reservation = new Reservation
            {
                ApplicationId = _app.Id, VenueId = venue.Id, StartUtc = s.Start.Value, EndUtc = s.End!.Value, BlockStartUtc = blockStart, BlockEndUtc = blockEnd,
                AttendeeCount = s.Attendees!.Value, Status = ReservationStatus.Tentative, CalendarState = CalendarPushState.Pending,
                AttendantNeeds = s.Attendees > 100 ? "Two attendants: one at the gate, one at the venue." : "One attendant at the venue for arrival and departure.",
                CreatedAt = at, CreatedBy = s.Scheduler.Id,
            };
            foreach (var (resource, quantity) in s.Resources)
                _reservation.Allocations.Add(new ResourceAllocation { ResourceId = resource.Id, Quantity = quantity, CreatedAt = at, CreatedBy = s.Scheduler.Id });
            db.Reservations.Add(_reservation);
            var push = new OutboxMessage
            {
                Type = SchedulingService.CalendarPushMessage, Payload = JsonSerializer.Serialize(new { reservationId = _reservation.Id, applicationId = _app.Id }),
                NextAttemptAt = at, CorrelationId = NextCorrelation(), CreatedAt = at, CreatedBy = s.Scheduler.Id,
            };
            db.OutboxMessages.Add(push);
            Move(Transition.Reserve, s.Scheduler, at, $"{venue.Name}, {s.Start:yyyy-MM-dd HH:mm} UTC");
            Notify("reservation.tentative", at);

            // The worker's calendar push a couple of minutes later (A-27).
            var pushed = at.AddMinutes(2);
            switch (s.Calendar)
            {
                case CalendarPushState.Pushed:
                    _reservation.CalendarState = CalendarPushState.Pushed;
                    _reservation.ExternalReservationId = $"CAL-{_reservation.Id.ToString("N")[..10].ToUpperInvariant()}";
                    _reservation.CalendarPushedAt = pushed;
                    Done(push, pushed);
                    Log(w.System, pushed, "Calendar.HoldPushed", nameof(Reservation), _reservation.Id, $"Hold {_reservation.ExternalReservationId} accepted by the regional calendar");
                    break;
                case CalendarPushState.Conflict when s.CalendarConflict is { } ext:
                    _reservation.CalendarState = CalendarPushState.Conflict;
                    Done(push, pushed);
                    db.ReconciliationItems.Add(new ReconciliationItem
                    {
                        ApplicationId = _app.Id, ReservationId = _reservation.Id, ExternalEventId = ext.ExternalId, SourceDescription = $"{ext.ExternalId} · {ext.Title}",
                        ConflictStartUtc = ext.Start, ConflictEndUtc = ext.End, Alternatives = s.Alternatives.ToList(), CreatedAt = pushed, CreatedBy = SeedMarker,
                    });
                    Log(w.System, pushed, "Calendar.Conflict", nameof(Reservation), _reservation.Id, $"Regional calendar conflict with {ext.ExternalId}; approval blocked until reconciled");
                    break;
                case CalendarPushState.Failed:
                    _reservation.CalendarState = CalendarPushState.Failed;
                    push.Status = OutboxStatus.Failed;
                    push.Attempts = 5;
                    push.LastError = "Calendar unavailable: HTTP 503 Service Unavailable";
                    push.ProcessedAt = pushed.AddMinutes(30);
                    Log(w.System, pushed, "Calendar.PushFailed", nameof(Reservation), _reservation.Id, "Hold not placed: HTTP 503 Service Unavailable");
                    break;
                default:
                    push.NextAttemptAt = w.Anchor; // still queued: the worker places the hold on its next run
                    break;
            }
            return Task.CompletedTask;
        }

        private static void Done(OutboxMessage message, DateTimeOffset at)
        {
            message.Status = OutboxStatus.Done;
            message.Attempts = 1;
            message.ProcessedAt = at;
        }

        private Task SubmitForApprovalAsync(DateTimeOffset at)
        {
            Move(Transition.SubmitForApproval, s.Scheduler, at);
            return Task.CompletedTask;
        }

        private Task ApproveAsync(DateTimeOffset at)
        {
            RequireSeparation();
            _reservation!.Status = ReservationStatus.Confirmed;
            const string notes = "Approved: calendar reconciled, resources confirmed and conditions standard.";
            db.ApprovalDecisions.Add(new ApprovalDecision
            {
                ApplicationId = _app.Id, ApproverUserId = s.Approver.Id, ApproverName = s.Approver.Name, Outcome = ApprovalOutcome.Approved, Notes = notes,
                DecidedAt = at, CreatedAt = at, CreatedBy = s.Approver.Id,
            });
            Move(Transition.Approve, s.Approver, at, notes);
            Notify("approval.approved", at);
            return Task.CompletedTask;
        }

        private Task RejectApprovalAsync(DateTimeOffset at)
        {
            RequireSeparation();
            db.ApprovalDecisions.Add(new ApprovalDecision
            {
                ApplicationId = _app.Id, ApproverUserId = s.Approver.Id, ApproverName = s.Approver.Name, Outcome = ApprovalOutcome.Rejected, Notes = s.Reason!,
                DecidedAt = at, CreatedAt = at, CreatedBy = s.Approver.Id,
            });
            _app.DecisionReason = s.Reason;
            Release("Rejected at approval", at);
            Move(Transition.RejectApproval, s.Approver, at, s.Reason);
            Notify("approval.rejected", at, new() { ["Reason"] = s.Reason! });
            return Task.CompletedTask;
        }

        private void RequireSeparation()
        {
            if (s.Approver.Id == s.Reviewer.Id) throw new InvalidOperationException($"{s.Key}: approver reviewed this case (separation of duties).");
        }

        private Task AcknowledgeWeatherAsync(DateTimeOffset at)
        {
            _app.WeatherAcknowledgedAt = at;
            _app.WeatherAcknowledgedBy = s.Approver.Name;
            _app.WeatherAcknowledgementNote = WeatherNote;
            Log(s.Approver, at, "Weather.Acknowledged", nameof(PermitApplication), _app.Id, "Severe weather advisory acknowledged", reason: WeatherNote);
            return Task.CompletedTask;
        }

        private Task AssignAsync(DateTimeOffset at)
        {
            db.AttendantAssignments.Add(new AttendantAssignment { ApplicationId = _app.Id, UserId = s.Attendant.Id, UserName = s.Attendant.Name, CreatedAt = at, CreatedBy = s.Approver.Id });
            Log(s.Approver, at, "Attendant.Assigned", nameof(PermitApplication), _app.Id, $"{s.Attendant.Name} assigned to {_app.Reference}");
            return Task.CompletedTask;
        }

        private async Task IssueAsync(DateTimeOffset at)
        {
            var number = await ReferenceGenerator.NextAsync(db, Limits.PermitPrefix, at.Year, ct);
            _permit = new Permit { ApplicationId = _app.Id, Number = number, CurrentVersion = 0, CreatedAt = at, CreatedBy = s.Approver.Id };
            db.Permits.Add(_permit);
            var pdf = await NewVersionAsync(at, PermitService.DefaultConditions, null);
            foreach (var (label, i) in PermitService.DefaultReadiness.Select((l, i) => (l, i)))
            {
                var item = new ReadinessChecklistItem { ApplicationId = _app.Id, Label = label, Order = i, CreatedAt = at, CreatedBy = s.Approver.Id };
                _readiness.Add(item);
                db.ReadinessItems.Add(item);
            }
            Move(Transition.IssuePermit, s.Approver, at, $"Permit {number} v1 issued");
            Log(s.Approver, at, "Permit.Issued", nameof(Permit), _permit.Id, $"{number} v1 issued by {s.Approver.Name} ({pdf.FileName})");
            Notify("permit.issued", at);
        }

        /// <summary>One immutable version: signed token (only its hash and short code are kept) and the rendered PDF in object storage.</summary>
        private async Task<Attachment> NewVersionAsync(DateTimeOffset at, string conditions, string? reason)
        {
            var permit = _permit!;
            var reservation = _reservation!;
            var venue = s.Venue!;
            var next = permit.CurrentVersion + 1;
            var expires = reservation.EndUtc.AddHours(w.TokenGraceHours);
            var (token, hash, shortCode) = PermitService.CreateToken(seeder.Signer, permit.Id, next, expires);
            var zone = SeedWorld.Zone(venue);
            var localStart = TimeZoneInfo.ConvertTime(reservation.StartUtc, zone);
            var localEnd = TimeZoneInfo.ConvertTime(reservation.EndUtc, zone);
            var bytes = seeder.Pdf.RenderPermit(new PermitPdfModel(permit.Number, next, _app.Reference, s.ApplicantName, s.Title, s.Category.Name,
                venue.Name, venue.Cemetery!.Name, $"{localStart:dddd d MMMM yyyy, HH:mm}–{localEnd:HH:mm}", venue.Cemetery.TimeZoneId, reservation.AttendeeCount,
                conditions, s.Approver.Name, at, token, PermitService.FormatShortCode(shortCode), reason));
            var attachment = await StoreAsync(s.Approver, at, AttachmentPurpose.PermitPdf, DocumentType.Other, $"{permit.Number}-v{next}.pdf", bytes, "application/pdf");
            attachment.ApplicationId = _app.Id;
            permit.Versions.Add(new PermitVersion
            {
                PermitId = permit.Id, VersionNumber = next, IssuedBy = s.Approver.Id, IssuedByName = s.Approver.Name, IssuedAt = at, VenueName = venue.Name,
                StartUtc = reservation.StartUtc, EndUtc = reservation.EndUtc, Conditions = conditions, ReissueReason = reason, TokenHash = hash, ShortCode = shortCode,
                TokenExpiresAt = expires, PdfObjectKey = attachment.ObjectKey, PdfSha256 = attachment.Sha256, CreatedAt = at, CreatedBy = s.Approver.Id,
            });
            permit.CurrentVersion = next;
            return attachment;
        }

        private async Task ReissueAsync(DateTimeOffset at)
        {
            const string reason = "Conditions updated to name the accessible shuttle drop-off point.";
            var permit = _permit!;
            var current = permit.Versions.First(v => v.VersionNumber == permit.CurrentVersion);
            current.Status = PermitVersionStatus.Superseded;
            await NewVersionAsync(at, current.Conditions + "\nUse the accessible shuttle drop-off at the east gate.", reason);
            permit.Status = PermitStatus.Active;
            Log(s.Approver, at, "Permit.Reissued", nameof(Permit), permit.Id, $"{permit.Number} v{permit.CurrentVersion} issued; v{current.VersionNumber} superseded", reason: reason);
            Notify("permit.reissued", at, new() { ["Reason"] = reason });
        }

        private Task RevokeAsync(DateTimeOffset at)
        {
            const string reason = "Organizer breached the amplified sound condition during the rehearsal.";
            var permit = _permit!;
            var current = permit.Versions.First(v => v.VersionNumber == permit.CurrentVersion);
            current.Status = PermitVersionStatus.Revoked;
            current.RevokedAt = at;
            current.RevokedBy = s.Approver.Name;
            current.RevokedReason = reason;
            permit.Status = PermitStatus.Revoked;
            Log(s.Approver, at, "Permit.Revoked", nameof(Permit), permit.Id, $"{permit.Number} v{current.VersionNumber} revoked", reason: reason);
            Notify("permit.revoked", at, new() { ["Reason"] = reason });
            return Task.CompletedTask;
        }

        private Task CancelAsync(DateTimeOffset at)
        {
            if (_reservation is not null) Release("Case cancelled", at);
            if (_permit is { } permit)
            {
                permit.Status = PermitStatus.Cancelled;
                foreach (var v in permit.Versions.Where(v => v.Status == PermitVersionStatus.Active)) v.Status = PermitVersionStatus.Cancelled;
            }
            _app.DecisionReason = s.Reason;
            Move(Transition.Cancel, s.Canceller, at, s.Reason);
            Notify("case.cancelled", at, new() { ["Reason"] = s.Reason! });
            return Task.CompletedTask;
        }

        /// <summary>As SchedulingService.ReleaseAll: reservation and allocations released, external hold released by the worker.</summary>
        private void Release(string reason, DateTimeOffset at)
        {
            var reservation = _reservation!;
            if (reservation.Status == ReservationStatus.Released) return;
            reservation.Status = ReservationStatus.Released;
            reservation.ReleaseReason = reason;
            foreach (var allocation in reservation.Allocations) allocation.Released = true;
            if (reservation.CalendarState is CalendarPushState.Pushed or CalendarPushState.Pending or CalendarPushState.Failed)
            {
                var release = new OutboxMessage
                {
                    Type = SchedulingService.CalendarReleaseMessage, Payload = JsonSerializer.Serialize(new { reservationId = reservation.Id, applicationId = _app.Id }),
                    NextAttemptAt = at, CorrelationId = NextCorrelation(), CreatedAt = at, CreatedBy = s.Canceller.Id,
                };
                Done(release, at.AddMinutes(2));
                db.OutboxMessages.Add(release);
                Log(w.System, at.AddMinutes(2), "Calendar.HoldReleased", nameof(Reservation), reservation.Id, "Hold released in the regional calendar");
            }
        }

        // ------------------------------------------------------------------ event day

        private async Task EventDayAsync()
        {
            if (s.Start is not { } start || _permit is null || _reservation is null || s.Path == SeedPath.CancelledPermitted) return;
            var end = s.End!.Value;
            var attendant = s.Attendant;
            var checkedIn = SeedMix.CheckedIn(s.Path);
            for (var i = 0; i < s.ReadinessDone && i < _readiness.Count; i++)
            {
                var at = checkedIn ? start.AddMinutes(-50 + i * 5) : Min(w.Anchor.AddMinutes(-40 + i * 5), start.AddMinutes(-50 + i * 5));
                var item = _readiness[i];
                item.Done = true;
                item.DoneBy = attendant.Name;
                item.DoneAt = at;
                Log(attendant, at, "Readiness.Changed", nameof(ReadinessChecklistItem), item.Id, $"Done: {item.Label}");
            }
            if (!checkedIn) return;

            var current = _permit.Versions.First(v => v.VersionNumber == _permit.CurrentVersion);
            var checkInAt = start.AddMinutes(-15);
            var checkIn = new CheckIn
            {
                ApplicationId = _app.Id, PermitVersionId = current.Id, AttendantUserId = attendant.Id, AttendantName = attendant.Name, CheckedInAt = checkInAt,
                CreatedAt = checkInAt, CreatedBy = attendant.Id,
            };
            db.CheckIns.Add(checkIn);
            Move(Transition.CheckIn, attendant, checkInAt, $"Permit {_permit.Number} v{current.VersionNumber} verified");

            var k = 0;
            foreach (var incident in s.Incidents)
                await IncidentAsync(incident, Min(start.AddMinutes(25 + 10 * k++), w.Anchor.AddMinutes(-10)), end);

            if (s.Path is SeedPath.EventCompleted or SeedPath.Closed)
            {
                var outAt = end.AddMinutes(10);
                checkIn.CheckedOutAt = outAt;
                checkIn.Notes = "All guests departed; venue left tidy.";
                Log(attendant, outAt, "CheckIn.CheckedOut", nameof(CheckIn), checkIn.Id, "Departure recorded");
                Move(Transition.Complete, attendant, end.AddMinutes(20));
            }
            if (s.Path == SeedPath.Closed)
                Move(Transition.Close, s.Approver, end.AddDays(1).AddHours(2), "Event reviewed; no unresolved high or critical incidents.");
        }

        private async Task IncidentAsync(IncidentPlan plan, DateTimeOffset at, DateTimeOffset end)
        {
            var reference = await ReferenceGenerator.NextAsync(db, Limits.IncidentPrefix, at.Year, ct);
            var owner = w.U("manager.echo");
            var reporter = s.Attendant;
            var day = DateOnly.FromDateTime(at.UtcDateTime);
            var incident = new Incident
            {
                ApplicationId = _app.Id, Reference = reference, Category = plan.Category, Severity = plan.Severity, Status = IncidentStatus.Open,
                Description = plan.Description, ReportedBy = reporter.Id, ReportedByName = reporter.Name, OwnerUserId = owner.Id, OwnerName = owner.Name,
                DueDate = plan.Overdue ? day.AddDays(1) : day.AddDays(3), CorrectiveAction = "Owner to confirm the corrective action with the site team.",
                IsOverdue = plan.Overdue, CreatedAt = at, CreatedBy = reporter.Id,
            };
            db.Incidents.Add(incident);
            Log(reporter, at, "Incident.Created", nameof(Incident), incident.Id, $"{reference} {plan.Severity}: {plan.Category}");
            if (plan.Photo)
            {
                var photoAt = at.AddMinutes(3);
                var photo = s.No % 2 == 0
                    ? await StoreAsync(reporter, photoAt, AttachmentPurpose.IncidentPhoto, DocumentType.Other, $"incident-{reference}.png", SyntheticFiles.Png(64, 48, (120, 40, 40), (250, 245, 235)), "image/png")
                    : await StoreAsync(reporter, photoAt, AttachmentPurpose.IncidentPhoto, DocumentType.Other, $"incident-{reference}.jpg", SyntheticFiles.Jpeg(96), "image/jpeg");
                photo.ApplicationId = _app.Id;
                photo.IncidentId = incident.Id;
                Log(reporter, photoAt, "Attachment.Uploaded", nameof(Incident), incident.Id, $"Photo {photo.FileName} added to {reference}");
            }
            if (plan.Status == IncidentStatus.Escalated || plan.Severity == IncidentSeverity.Critical)
            {
                var escalatedAt = at.AddMinutes(5);
                incident.Status = IncidentStatus.Escalated;
                incident.EscalatedAt = escalatedAt;
                Log(reporter, escalatedAt, "Incident.Escalated", nameof(Incident), incident.Id, $"{reference} escalated to operations management",
                    reason: "Severity requires an operations manager decision.");
            }
            if (plan.Status == IncidentStatus.Resolved)
            {
                var resolvedAt = end.AddHours(3);
                const string resolution = "Followed up with the organizer and site team; corrective action completed and recorded.";
                incident.Status = IncidentStatus.Resolved;
                incident.Resolution = resolution;
                incident.ResolvedBy = owner.Name;
                incident.ResolvedAt = resolvedAt;
                incident.IsOverdue = false;
                Log(owner, resolvedAt, "Incident.Resolved", nameof(Incident), incident.Id, $"{reference} resolved", reason: resolution);
            }
        }

        // ------------------------------------------------------------------ comments

        private void Comments(DateTimeOffset workflowEnd)
        {
            if (s.No % 2 == 0)
                Comment(s.Owner, s.Created.AddMinutes(30), s.No % 4 == 0
                    ? "Spoke with the applicant: a morning slot is preferred, but an hour either way is fine."
                    : "Applicant confirmed the honoree's details by phone.");
            if (s.CollisionNote is { } note && s.Path is SeedPath.Eligible or SeedPath.Submitted)
                Comment(s.Path == SeedPath.Eligible ? s.Scheduler : s.Reviewer, Min(workflowEnd.AddMinutes(30), w.Anchor.AddMinutes(-10)),
                    $"The requested slot clashes with {note}; scheduling will offer the nearest free alternatives.");
            if (s.Path == SeedPath.EventCompleted && s.Incidents.Any(i => i.Severity == IncidentSeverity.Critical) && s.End is { } end)
                Comment(s.Approver, Min(end.AddHours(2), w.Anchor.AddMinutes(-5)), "Closure on hold until the medical incident review is complete.");
            if (s.Path == SeedPath.Closed && s.End is { } closedEnd)
                Comment(s.Approver, closedEnd.AddDays(1).AddHours(1), "Event ran as planned; closure confirmed after the incident review.");
        }

        private void Comment(SeedUser author, DateTimeOffset at, string body)
        {
            var comment = new Comment { ApplicationId = _app.Id, AuthorUserId = author.Id, AuthorName = author.Name, Body = body, CreatedAt = at, CreatedBy = author.Id };
            db.Comments.Add(comment);
            Log(author, at, "Comment.Added", nameof(Comment), comment.Id, $"Comment added by {author.Name}");
        }

        // ------------------------------------------------------------------ transitions, audit, notifications

        /// <summary>Same rule check and audit shape as ServiceBase.ApplyTransition; refuses a move the table does not allow or a wrong role.</summary>
        private void Move(Transition transition, SeedUser actor, DateTimeOffset at, string? reason = null)
        {
            var rule = Workflow.Rule(transition);
            if (!Workflow.IsAllowed(transition, _app.Status))
                throw new InvalidOperationException($"{s.Key}: {rule.Id} is not allowed from {_app.Status}.");
            if (!actor.Roles.Contains(rule.Role))
                throw new InvalidOperationException($"{s.Key}: {actor.UserName} does not hold {rule.Role} for {rule.Id}.");
            var from = _app.Status;
            _app.Status = rule.To;
            _app.StatusChangedAt = at;
            Log(actor, at, $"Transition.{transition}", nameof(PermitApplication), _app.Id, $"{rule.Id} {rule.Name}: {Workflow.Display(from)} → {Workflow.Display(rule.To)}",
                new { status = from.ToString() }, new { status = rule.To.ToString() }, reason);
        }

        private void Log(SeedUser actor, DateTimeOffset at, string action, string entityType, object entityId, string summary,
            object? before = null, object? after = null, string? reason = null)
        {
            if (at > _last) _last = at;
            db.AuditEvents.Add(new AuditEvent
            {
                At = at, ActorUserId = actor.Id, ActorName = actor.Name, Action = action, EntityType = entityType, EntityId = entityId.ToString() ?? "",
                ApplicationId = _app.Id, Summary = summary.Length > 500 ? summary[..500] : summary,
                Before = before is null ? null : JsonSerializer.Serialize(before, Json), After = after is null ? null : JsonSerializer.Serialize(after, Json),
                Reason = reason, CorrelationId = NextCorrelation(),
            });
        }

        private string NextCorrelation() => $"seed{s.No:D3}{++_seq:D4}";

        /// <summary>Only the 25 planned notifications are stored (the brief's count); delivery history matches their state.</summary>
        private void Notify(string templateCode, DateTimeOffset at, Dictionary<string, string>? values = null)
        {
            if (!s.Notify.TryGetValue(templateCode, out var status) || !_notified.Add(templateCode)) return;
            if (!w.Templates.TryGetValue(templateCode, out var template)) return;
            var zone = s.Venue is { } v ? SeedWorld.Zone(v) : TimeZoneInfo.Utc;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ApplicantName"] = s.ApplicantName,
                ["Reference"] = _app.Reference,
                ["EventTitle"] = s.Title,
                ["EventDate"] = s.Start is { } st ? TimeZoneInfo.ConvertTime(st, zone).ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture) : "",
                ["Venue"] = s.Venue?.Name ?? "",
                ["PermitNumber"] = _permit?.Number ?? "",
            };
            if (values is not null) foreach (var (k, value) in values) fields[k] = value;
            var n = new Notification
            {
                ApplicationId = _app.Id, ApplicationReference = _app.Reference, Channel = template.Channel, Recipient = s.Email, TemplateCode = template.Code,
                Subject = NotificationOutbox.Fill(template.Subject, fields), Body = NotificationOutbox.Fill(template.Body, fields), Status = status,
                IdempotencyKey = $"seed-ntf-{s.No:D3}-{templateCode}", CorrelationId = NextCorrelation(), CreatedAt = at, CreatedBy = SeedMarker,
            };
            void Change(NotificationStatus? from, NotificationStatus to, string source, string detail, DateTimeOffset when) =>
                n.StatusChanges.Add(new NotificationStatusChange { From = from, To = to, Source = source, Detail = detail, At = when, CreatedAt = when, CreatedBy = SeedMarker });
            Change(null, NotificationStatus.Queued, "workflow", templateCode, at);
            switch (status)
            {
                case NotificationStatus.Queued:
                    n.NextAttemptAt = at;
                    break;
                case NotificationStatus.Sent:
                case NotificationStatus.Delivered:
                    n.Attempts = 1;
                    n.GatewayMessageId = MessageId(s.No, templateCode);
                    n.SentAt = at.AddMinutes(1);
                    Change(NotificationStatus.Queued, NotificationStatus.Sent, "gateway", "Accepted by the gateway", at.AddMinutes(1));
                    if (status == NotificationStatus.Delivered)
                    {
                        n.DeliveredAt = at.AddMinutes(3);
                        Change(NotificationStatus.Sent, NotificationStatus.Delivered, "webhook", "Delivery confirmed by signed webhook", at.AddMinutes(3));
                    }
                    break;
                case NotificationStatus.Failed:
                    n.Attempts = 5;
                    n.LastError = "HTTP 500 Internal Server Error";
                    n.FailedAt = at.AddMinutes(40);
                    Change(NotificationStatus.Queued, NotificationStatus.Failed, "retry", "Gave up after 5 attempts: HTTP 500 Internal Server Error", at.AddMinutes(40));
                    break;
            }
            db.Notifications.Add(n);
        }

        private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    }
}
