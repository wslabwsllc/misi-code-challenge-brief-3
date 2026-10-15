namespace EventPermits.Domain;

/// <summary>Case status (brief §5). Transitions between them are defined only in <see cref="Workflow"/>.</summary>
public enum CaseStatus
{
    Draft,
    Submitted,
    NeedsInformation,
    Eligible,
    Rejected,
    TentativelyReserved,
    PendingApproval,
    Approved,
    Permitted,
    CheckedIn,
    EventCompleted,
    Closed,
    Cancelled,
}

/// <summary>Role names (brief §3). A user may hold several roles (A-03).</summary>
public static class Roles
{
    public const string IntakeCoordinator = "IntakeCoordinator";
    public const string EligibilityReviewer = "EligibilityReviewer";
    public const string SchedulingOfficer = "SchedulingOfficer";
    public const string OperationsManager = "OperationsManager";
    public const string EventAttendant = "EventAttendant";
    public const string ComplianceAuditor = "ComplianceAuditor";
    public const string SystemAdmin = "SystemAdmin";

    public static readonly string[] All =
    [
        IntakeCoordinator, EligibilityReviewer, SchedulingOfficer, OperationsManager, EventAttendant, ComplianceAuditor, SystemAdmin,
    ];

    public static string Display(string role) => role switch
    {
        IntakeCoordinator => "Intake Coordinator",
        EligibilityReviewer => "Eligibility Reviewer",
        SchedulingOfficer => "Scheduling Officer",
        OperationsManager => "Operations Manager",
        EventAttendant => "Event Attendant",
        ComplianceAuditor => "Compliance Auditor",
        SystemAdmin => "System Admin",
        _ => role,
    };
}

public enum DocumentType
{
    ProofOfAffiliation,
    EventPlan,
    InsuranceCertificate,
    SitePlan,
    Other,
}

public enum AttachmentPurpose
{
    ApplicationDocument,
    InformationResponse,
    IncidentPhoto,
    PermitPdf,
}

public enum ReviewDecision
{
    Pass,
    Reject,
    RequestInformation,
}

public enum ApprovalOutcome
{
    Approved,
    Rejected,
}

public enum InformationRequestStatus
{
    Open,
    Answered,
}

public enum ReservationStatus
{
    Tentative,
    Confirmed,
    Released,
}

/// <summary>State of the reservation hold in the external regional calendar (A-27).</summary>
public enum CalendarPushState
{
    NotPushed,
    Pending,
    Pushed,
    Conflict,
    Failed,
}

public enum PermitStatus
{
    Active,
    Revoked,
    Cancelled,
}

public enum PermitVersionStatus
{
    Active,
    Superseded,
    Revoked,
    Cancelled,
}

public enum IncidentSeverity
{
    Low,
    Medium,
    High,
    Critical,
}

public enum IncidentStatus
{
    Open,
    Escalated,
    Resolved,
}

public enum NotificationChannel
{
    Email,
    Sms,
}

/// <summary>Delivery order Queued &lt; Sent &lt; Delivered; Failed is terminal unless retried (A-20).</summary>
public enum NotificationStatus
{
    Queued,
    Sent,
    Delivered,
    Failed,
}

public enum RegistryEligibility
{
    Valid,
    Flagged,
    Unknown,
}

public enum VerificationOutcome
{
    Matched,
    NotFound,
    Pending,
}

public enum WeatherAdvisory
{
    None,
    Advisory,
    Severe,
}

public enum IntegrationKind
{
    Registry,
    Calendar,
    NotificationGateway,
    ObjectStorage,
    Weather,
    PartnerFeed,
    Geocoding,
}

public enum AttemptOutcome
{
    Success,
    ClientError,
    Transient,
    Timeout,
    Offline,
}

public enum ReconciliationStatus
{
    Open,
    Resolved,
}

public enum ImportBatchStatus
{
    Running,
    Completed,
    CompletedWithErrors,
    Failed,
    Duplicate,
}

public enum WebhookOutcome
{
    Accepted,
    Duplicate,
    Ignored,
    RejectedSignature,
    RejectedTimestamp,
    RejectedPayload,
}

public enum OutboxStatus
{
    Pending,
    Done,
    Failed,
}
