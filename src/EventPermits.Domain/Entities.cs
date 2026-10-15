namespace EventPermits.Domain;

/// <summary>
/// Base for every business row: random GUID key, UTC audit columns and a row version
/// (PostgreSQL <c>xmin</c>) used for optimistic concurrency. Rows are never hard-deleted.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "";
    public uint Version { get; set; }
}

// ------------------------------------------------------------------ reference data

public sealed class Cemetery : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    /// <summary>IANA time zone; event times are stored in UTC and shown in this zone.</summary>
    public string TimeZoneId { get; set; } = "America/New_York";
    public bool IsActive { get; set; } = true;
    public List<Venue> Venues { get; set; } = [];
}

public sealed class EventCategory : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Documents an application of this category must carry before submission.</summary>
    public List<DocumentType> RequiredDocuments { get; set; } = [];
    /// <summary>Items the Eligibility Reviewer must resolve before passing.</summary>
    public List<string> ReviewChecklist { get; set; } = [];
    public bool IsActive { get; set; } = true;
}

public sealed class Venue : Entity
{
    public Guid CemeteryId { get; set; }
    public Cemetery? Cemetery { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public int Capacity { get; set; }
    public int SetupBufferMinutes { get; set; } = 30;
    public int TeardownBufferMinutes { get; set; } = 30;
    public List<OpeningHours> OpeningHours { get; set; } = [];
    /// <summary>Codes of the event categories this venue accepts.</summary>
    public List<string> AllowedCategoryCodes { get; set; } = [];
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool CoordinatesVerified { get; set; }
    public string? GeocodeLabel { get; set; }
    public double? GeocodeConfidence { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Venue-local opening hours for one weekday.</summary>
public sealed class OpeningHours
{
    public DayOfWeek Day { get; set; }
    public TimeOnly Opens { get; set; }
    public TimeOnly Closes { get; set; }
}

/// <summary>Hard closure of a venue. Nobody may reserve inside it (Scheduling cannot override).</summary>
public sealed class VenueBlackout : Entity
{
    public Guid VenueId { get; set; }
    public Venue? Venue { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public string Reason { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

/// <summary>Bookable resource stock at one cemetery (chairs, tents, attendants, AV kits...).</summary>
public sealed class Resource : Entity
{
    public Guid CemeteryId { get; set; }
    public Cemetery? Cemetery { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "each";
    public int TotalQuantity { get; set; }
    public bool IsActive { get; set; } = true;
}

// ------------------------------------------------------------------ the case

public sealed class Applicant : Entity
{
    public string FullName { get; set; } = "";
    public string Organization { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    /// <summary>Reference checked against the fictional Applicant Registry.</summary>
    public string RegistryReference { get; set; } = "";
}

/// <summary>The case ("Application" in the brief). Aggregate root: child writes bump its version.</summary>
public sealed class PermitApplication : Entity
{
    public string Reference { get; set; } = "";
    public CaseStatus Status { get; set; } = CaseStatus.Draft;
    public string OwnerUserId { get; set; } = "";
    public Guid ApplicantId { get; set; }
    public Applicant? Applicant { get; set; }
    public EventDetails? Event { get; set; }
    public int WizardStep { get; set; } = 1;
    public string? ClientRequestKey { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset StatusChangedAt { get; set; }
    public string? DecisionReason { get; set; }
    /// <summary>Set when a manager acknowledged a severe weather advisory for the event date (A-19).</summary>
    public DateTimeOffset? WeatherAcknowledgedAt { get; set; }
    public string? WeatherAcknowledgedBy { get; set; }
    public string? WeatherAcknowledgementNote { get; set; }

    public List<RequestedResource> RequestedResources { get; set; } = [];
    public List<Attachment> Attachments { get; set; } = [];
    public List<Comment> Comments { get; set; } = [];
    public List<InformationRequest> InformationRequests { get; set; } = [];
    public List<EligibilityReview> Reviews { get; set; } = [];
    public List<ApprovalDecision> Approvals { get; set; } = [];
    public List<Reservation> Reservations { get; set; } = [];
    public List<CheckIn> CheckIns { get; set; } = [];
    public List<Incident> Incidents { get; set; } = [];
    public List<AttendantAssignment> Assignments { get; set; } = [];
    public List<ReadinessChecklistItem> ReadinessItems { get; set; } = [];
    public Permit? Permit { get; set; }

    public Reservation? ActiveReservation => Reservations.FirstOrDefault(r => r.Status != ReservationStatus.Released);
}

/// <summary>The proposed event of an application ("Event" in the brief).</summary>
public sealed class EventDetails : Entity
{
    public Guid ApplicationId { get; set; }
    public string Title { get; set; } = "";
    public Guid? CategoryId { get; set; }
    public EventCategory? Category { get; set; }
    public Guid? CemeteryId { get; set; }
    public Cemetery? Cemetery { get; set; }
    public Guid? PreferredVenueId { get; set; }
    public Venue? PreferredVenue { get; set; }
    public DateTimeOffset? StartUtc { get; set; }
    public int? DurationMinutes { get; set; }
    public int? AttendeeCount { get; set; }
    public string Description { get; set; } = "";
    public string SpecialNeeds { get; set; } = "";

    public DateTimeOffset? EndUtc => StartUtc is { } s && DurationMinutes is { } d ? s.AddMinutes(d) : null;
}

public sealed class RequestedResource : Entity
{
    public Guid ApplicationId { get; set; }
    public Guid ResourceId { get; set; }
    public Resource? Resource { get; set; }
    public int Quantity { get; set; }
}

public sealed class Attachment : Entity
{
    public Guid? ApplicationId { get; set; }
    public Guid? IncidentId { get; set; }
    public Guid? InformationRequestId { get; set; }
    public AttachmentPurpose Purpose { get; set; }
    public DocumentType DocumentType { get; set; } = DocumentType.Other;
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string ObjectKey { get; set; } = "";
    public string UploadedBy { get; set; } = "";
    public DateTimeOffset UploadedAt { get; set; }
}

public sealed class Comment : Entity
{
    public Guid ApplicationId { get; set; }
    public string AuthorUserId { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public string Body { get; set; } = "";
}

public sealed class EligibilityReview : Entity
{
    public Guid ApplicationId { get; set; }
    public string ReviewerUserId { get; set; } = "";
    public string ReviewerName { get; set; } = "";
    public ReviewDecision Decision { get; set; }
    /// <summary>Checklist item text to resolution (true = satisfied).</summary>
    public Dictionary<string, bool> Checklist { get; set; } = [];
    public string? Reason { get; set; }
    public bool RegistryOverride { get; set; }
    public string? OverrideReason { get; set; }
    public Guid? RegistryVerificationId { get; set; }
}

public sealed class InformationRequest : Entity
{
    public Guid ApplicationId { get; set; }
    public string RequestedBy { get; set; } = "";
    public string RequestedByName { get; set; } = "";
    public List<string> MissingItems { get; set; } = [];
    public string Message { get; set; } = "";
    public DateOnly DueDate { get; set; }
    public InformationRequestStatus Status { get; set; } = InformationRequestStatus.Open;
    public bool IsOverdue { get; set; }
    public string? ResponseText { get; set; }
    public string? RespondedBy { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
}

public sealed class Reservation : Entity
{
    public Guid ApplicationId { get; set; }
    public Guid VenueId { get; set; }
    public Venue? Venue { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    /// <summary>Occupied window including setup and teardown buffers; used by the conflict engine.</summary>
    public DateTimeOffset BlockStartUtc { get; set; }
    public DateTimeOffset BlockEndUtc { get; set; }
    public int AttendeeCount { get; set; }
    public ReservationStatus Status { get; set; } = ReservationStatus.Tentative;
    public string AttendantNeeds { get; set; } = "";
    public CalendarPushState CalendarState { get; set; } = CalendarPushState.NotPushed;
    public string? ExternalReservationId { get; set; }
    public DateTimeOffset? CalendarPushedAt { get; set; }
    public string? ReleaseReason { get; set; }
    public List<ResourceAllocation> Allocations { get; set; } = [];
}

public sealed class ResourceAllocation : Entity
{
    public Guid ReservationId { get; set; }
    public Guid ResourceId { get; set; }
    public Resource? Resource { get; set; }
    public int Quantity { get; set; }
    public bool Released { get; set; }
}

public sealed class ApprovalDecision : Entity
{
    public Guid ApplicationId { get; set; }
    public string ApproverUserId { get; set; } = "";
    public string ApproverName { get; set; } = "";
    public ApprovalOutcome Outcome { get; set; }
    public string Notes { get; set; } = "";
    public DateTimeOffset DecidedAt { get; set; }
}

public sealed class Permit : Entity
{
    public Guid ApplicationId { get; set; }
    public string Number { get; set; } = "";
    public PermitStatus Status { get; set; } = PermitStatus.Active;
    public int CurrentVersion { get; set; }
    public List<PermitVersion> Versions { get; set; } = [];
}

/// <summary>Immutable issued permit version. Only its status changes (superseded, revoked, cancelled).</summary>
public sealed class PermitVersion : Entity
{
    public Guid PermitId { get; set; }
    public int VersionNumber { get; set; }
    public PermitVersionStatus Status { get; set; } = PermitVersionStatus.Active;
    public string IssuedBy { get; set; } = "";
    public string IssuedByName { get; set; } = "";
    public DateTimeOffset IssuedAt { get; set; }
    public string VenueName { get; set; } = "";
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public string Conditions { get; set; } = "";
    public string? ReissueReason { get; set; }
    /// <summary>SHA-256 of the signed token; the token itself is only in the PDF/QR.</summary>
    public string TokenHash { get; set; } = "";
    public string ShortCode { get; set; } = "";
    public DateTimeOffset TokenExpiresAt { get; set; }
    public string PdfObjectKey { get; set; } = "";
    public string PdfSha256 { get; set; } = "";
    public string? RevokedReason { get; set; }
    public string? RevokedBy { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class AttendantAssignment : Entity
{
    public Guid ApplicationId { get; set; }
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public bool Active { get; set; } = true;
}

public sealed class ReadinessChecklistItem : Entity
{
    public Guid ApplicationId { get; set; }
    public string Label { get; set; } = "";
    public int Order { get; set; }
    public bool Done { get; set; }
    public string? DoneBy { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
}

public sealed class CheckIn : Entity
{
    public Guid ApplicationId { get; set; }
    public Guid PermitVersionId { get; set; }
    public string AttendantUserId { get; set; } = "";
    public string AttendantName { get; set; } = "";
    public DateTimeOffset CheckedInAt { get; set; }
    public DateTimeOffset? CheckedOutAt { get; set; }
    public string Notes { get; set; } = "";
}

public sealed class Incident : Entity
{
    public Guid ApplicationId { get; set; }
    public string Reference { get; set; } = "";
    public string Category { get; set; } = "";
    public IncidentSeverity Severity { get; set; }
    public IncidentStatus Status { get; set; } = IncidentStatus.Open;
    public string Description { get; set; } = "";
    public string ReportedBy { get; set; } = "";
    public string ReportedByName { get; set; } = "";
    public string? OwnerUserId { get; set; }
    public string? OwnerName { get; set; }
    public DateOnly? DueDate { get; set; }
    public string CorrectiveAction { get; set; } = "";
    public bool IsOverdue { get; set; }
    public DateTimeOffset? EscalatedAt { get; set; }
    public string? Resolution { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public List<Attachment> Photos { get; set; } = [];

    public bool BlocksClosure => Status != IncidentStatus.Resolved && Severity >= IncidentSeverity.High;
}

// ------------------------------------------------------------------ notifications

public sealed class NotificationTemplate : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public NotificationChannel Channel { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

/// <summary>An outbound message. Status Queued rows are the notification outbox processed by the worker.</summary>
public sealed class Notification : Entity
{
    public Guid? ApplicationId { get; set; }
    public string? ApplicationReference { get; set; }
    public NotificationChannel Channel { get; set; }
    public string Recipient { get; set; } = "";
    public string TemplateCode { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public NotificationStatus Status { get; set; } = NotificationStatus.Queued;
    public string IdempotencyKey { get; set; } = "";
    public string? GatewayMessageId { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public bool Manual { get; set; }
    public string CorrelationId { get; set; } = "";
    public List<NotificationStatusChange> StatusChanges { get; set; } = [];
}

public sealed class NotificationStatusChange : Entity
{
    public Guid NotificationId { get; set; }
    public NotificationStatus? From { get; set; }
    public NotificationStatus To { get; set; }
    /// <summary>Who caused it: gateway, webhook, poll, retry, manual.</summary>
    public string Source { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

/// <summary>Immutable record of every delivery webhook call, accepted or rejected.</summary>
public sealed class WebhookReceipt : Entity
{
    public string? EventId { get; set; }
    public string? MessageId { get; set; }
    public string? ReportedStatus { get; set; }
    public DateTimeOffset? OccurredAtUtc { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public WebhookOutcome Outcome { get; set; }
    public string Reason { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public Guid? NotificationId { get; set; }
}

// ------------------------------------------------------------------ audit, settings, users' views

/// <summary>Append-only audit history. Never updated or deleted (enforced by the persistence layer).</summary>
public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string ActorUserId { get; set; } = "";
    public string ActorName { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public Guid? ApplicationId { get; set; }
    public string Summary { get; set; } = "";
    public string? Before { get; set; }
    public string? After { get; set; }
    public string? Reason { get; set; }
    public string CorrelationId { get; set; } = "";
}

public sealed class RuleSetting : Entity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string Description { get; set; } = "";
    public int? Min { get; set; }
    public int? Max { get; set; }
}

public sealed class SavedView : Entity
{
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Register query string (filters, sort, page size).</summary>
    public string Query { get; set; } = "";
    public bool Deleted { get; set; }
}

public sealed class ReferenceCounter
{
    public string Prefix { get; set; } = "";
    public int Year { get; set; }
    public int Next { get; set; }
}

public sealed class SystemState
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

// ------------------------------------------------------------------ integrations

/// <summary>One outbound HTTP attempt, sanitised (no keys, tokens or secrets).</summary>
public sealed class IntegrationAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset At { get; set; }
    public IntegrationKind Integration { get; set; }
    public string Operation { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public Guid? ApplicationId { get; set; }
    public string Method { get; set; } = "";
    public string Url { get; set; } = "";
    public string RequestSummary { get; set; } = "";
    public int? StatusCode { get; set; }
    public AttemptOutcome Outcome { get; set; }
    public int DurationMs { get; set; }
    public int AttemptNumber { get; set; } = 1;
    public string? Error { get; set; }
    public string? ResponseExcerpt { get; set; }
}

public sealed class RegistryVerification : Entity
{
    public Guid? ApplicationId { get; set; }
    public string Reference { get; set; } = "";
    public VerificationOutcome Outcome { get; set; }
    public string? MatchedName { get; set; }
    public RegistryEligibility? Eligibility { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string RequestedBy { get; set; } = "";
    public string Detail { get; set; } = "";
    public Guid? AttemptId { get; set; }
}

public sealed class ExternalCalendarEvent : Entity
{
    public string ExternalId { get; set; } = "";
    public string VenueCode { get; set; } = "";
    public Guid? VenueId { get; set; }
    public string Title { get; set; } = "";
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }
    /// <summary>True when the event is this system's own pushed hold (not a conflict with itself).</summary>
    public Guid? OwnReservationId { get; set; }
    public bool Cancelled { get; set; }
}

public sealed class CalendarSyncRun : Entity
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Trigger { get; set; } = "";
    public bool Succeeded { get; set; }
    public int Imported { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public string? CursorBefore { get; set; }
    public string? CursorAfter { get; set; }
    public string? Error { get; set; }
}

public sealed class ReconciliationItem : Entity
{
    public Guid ApplicationId { get; set; }
    public Guid ReservationId { get; set; }
    public string? ExternalEventId { get; set; }
    public string SourceDescription { get; set; } = "";
    public DateTimeOffset? ConflictStartUtc { get; set; }
    public DateTimeOffset? ConflictEndUtc { get; set; }
    /// <summary>Alternative slot starts suggested by the calendar.</summary>
    public List<DateTimeOffset> Alternatives { get; set; } = [];
    public ReconciliationStatus Status { get; set; } = ReconciliationStatus.Open;
    public string? Resolution { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class WeatherSnapshot : Entity
{
    public Guid VenueId { get; set; }
    public string VenueCode { get; set; } = "";
    public DateOnly Date { get; set; }
    public string Summary { get; set; } = "";
    public double TemperatureC { get; set; }
    public double WindKph { get; set; }
    public int PrecipitationPct { get; set; }
    public WeatherAdvisory Advisory { get; set; }
    public DateTimeOffset SourceTimestampUtc { get; set; }
    public DateTimeOffset RetrievedAt { get; set; }
}

public sealed class GeocodeLookup : Entity
{
    public Guid VenueId { get; set; }
    public string Query { get; set; } = "";
    public bool Succeeded { get; set; }
    public string Detail { get; set; } = "";
    public List<GeocodeCandidate> Candidates { get; set; } = [];
}

public sealed class GeocodeCandidate : Entity
{
    public Guid LookupId { get; set; }
    public string Label { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Confidence { get; set; }
    public bool Selected { get; set; }
}

public sealed class ImportBatch : Entity
{
    public string BatchId { get; set; } = "";
    public string Source { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Checksum { get; set; } = "";
    public ImportBatchStatus Status { get; set; } = ImportBatchStatus.Running;
    public int RowsRead { get; set; }
    public int Inserted { get; set; }
    public int UpdatedRows { get; set; }
    public int UnchangedRows { get; set; }
    public int Rejected { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<string> MappingDiagnostics { get; set; } = [];
    public bool IsReplay { get; set; }
    public List<ImportRowError> Errors { get; set; } = [];
}

public sealed class ImportRowError : Entity
{
    public Guid BatchId { get; set; }
    public int RowNumber { get; set; }
    public string RawLine { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class PartnerEvent : Entity
{
    public string PartnerEventId { get; set; } = "";
    public string SiteCode { get; set; } = "";
    public Guid? VenueId { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }
    public int AttendeeCount { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid LastBatchId { get; set; }
}

/// <summary>Outbound integration commands other than notifications (calendar pushes) committed with the business change.</summary>
public sealed class OutboxMessage : Entity
{
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string CorrelationId { get; set; } = "";
}

public sealed class JobRun
{
    public long Id { get; set; }
    public string Job { get; set; } = "";
    public string Trigger { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool Succeeded { get; set; }
    public string Details { get; set; } = "";
}
