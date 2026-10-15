using EventPermits.Application.Services;
using EventPermits.Domain;

namespace EventPermits.Infrastructure.Seeding;

/// <summary>Where a seeded case ends up, and so which workflow history it gets (plan §3.4).</summary>
public enum SeedPath
{
    Draft,
    Submitted,
    NeedsInformation,
    Eligible,
    RejectedEligibility,
    CancelledSubmitted,
    CancelledEligible,
    TentativelyReserved,
    PendingApproval,
    Approved,
    RejectedApproval,
    CancelledReserved,
    CancelledPending,
    CancelledApproved,
    Permitted,
    CancelledPermitted,
    CheckedIn,
    EventCompleted,
    Closed,
}

/// <summary>The seed's case mix (120 applications, 50 with a reservation, 20 with a permit, 15 checked in).</summary>
public static class SeedMix
{
    public static readonly IReadOnlyList<(SeedPath Path, int Count)> Counts =
    [
        (SeedPath.Draft, 12), (SeedPath.Submitted, 14), (SeedPath.NeedsInformation, 14), (SeedPath.Eligible, 12),
        (SeedPath.RejectedEligibility, 10), (SeedPath.CancelledSubmitted, 4), (SeedPath.CancelledEligible, 4),
        (SeedPath.TentativelyReserved, 9), (SeedPath.PendingApproval, 9), (SeedPath.Approved, 6), (SeedPath.RejectedApproval, 3),
        (SeedPath.CancelledReserved, 1), (SeedPath.CancelledPending, 1), (SeedPath.CancelledApproved, 1),
        (SeedPath.Permitted, 4), (SeedPath.CancelledPermitted, 1), (SeedPath.CheckedIn, 3), (SeedPath.EventCompleted, 4), (SeedPath.Closed, 8),
    ];

    public static CaseStatus FinalStatus(SeedPath path) => path switch
    {
        SeedPath.Draft => CaseStatus.Draft,
        SeedPath.Submitted => CaseStatus.Submitted,
        SeedPath.NeedsInformation => CaseStatus.NeedsInformation,
        SeedPath.Eligible => CaseStatus.Eligible,
        SeedPath.RejectedEligibility or SeedPath.RejectedApproval => CaseStatus.Rejected,
        SeedPath.TentativelyReserved => CaseStatus.TentativelyReserved,
        SeedPath.PendingApproval => CaseStatus.PendingApproval,
        SeedPath.Approved => CaseStatus.Approved,
        SeedPath.Permitted => CaseStatus.Permitted,
        SeedPath.CheckedIn => CaseStatus.CheckedIn,
        SeedPath.EventCompleted => CaseStatus.EventCompleted,
        SeedPath.Closed => CaseStatus.Closed,
        _ => CaseStatus.Cancelled,
    };

    public static bool HasReservation(SeedPath p) => p is SeedPath.TentativelyReserved or SeedPath.PendingApproval or SeedPath.Approved
        or SeedPath.RejectedApproval or SeedPath.CancelledReserved or SeedPath.CancelledPending or SeedPath.CancelledApproved
        or SeedPath.Permitted or SeedPath.CancelledPermitted or SeedPath.CheckedIn or SeedPath.EventCompleted or SeedPath.Closed;

    public static bool ReservationReleased(SeedPath p) => p is SeedPath.RejectedApproval or SeedPath.CancelledReserved or SeedPath.CancelledPending
        or SeedPath.CancelledApproved or SeedPath.CancelledPermitted;

    public static bool PassesEligibility(SeedPath p) => p is SeedPath.Eligible or SeedPath.CancelledEligible || HasReservation(p);

    public static bool ReachesApproval(SeedPath p) => p is SeedPath.PendingApproval or SeedPath.Approved or SeedPath.RejectedApproval
        or SeedPath.CancelledPending or SeedPath.CancelledApproved or SeedPath.Permitted or SeedPath.CancelledPermitted
        or SeedPath.CheckedIn or SeedPath.EventCompleted or SeedPath.Closed;

    public static bool Approved(SeedPath p) => p is SeedPath.Approved or SeedPath.CancelledApproved or SeedPath.Permitted or SeedPath.CancelledPermitted
        or SeedPath.CheckedIn or SeedPath.EventCompleted or SeedPath.Closed;

    public static bool HasPermit(SeedPath p) => p is SeedPath.Permitted or SeedPath.CancelledPermitted or SeedPath.CheckedIn or SeedPath.EventCompleted or SeedPath.Closed;

    public static bool CheckedIn(SeedPath p) => p is SeedPath.CheckedIn or SeedPath.EventCompleted or SeedPath.Closed;
}

public sealed record SeedUser(string Id, string UserName, string Name, IReadOnlySet<string> Roles);

public enum RegistryPlan { None, Valid, Flagged, Unknown, NotFound, Pending }

public enum RequestPlan { None, Open, Overdue }

public enum WeatherPlan { None, SevereUnacknowledged, SevereAcknowledged }

public sealed record IncidentPlan(IncidentSeverity Severity, IncidentStatus Status, string Category, string Description, bool Overdue = false, bool Photo = false);

public sealed record ExternalPlan(string ExternalId, Venue Venue, string Title, DateTimeOffset Start, DateTimeOffset End);

public sealed record BlackoutPlan(Venue Venue, DateTimeOffset Start, DateTimeOffset End, string Reason);

public sealed record PartnerBatchPlan(string BatchId, string FileName, byte[] Content, DateTimeOffset StartedAt, bool Duplicate);

/// <summary>One planned case: everything needed to write a coherent history, decided before anything is stored.</summary>
public sealed class CaseSpec
{
    public int No { get; init; }
    public SeedPath Path { get; init; }
    public string Key => $"seed-case-{No:D3}";
    public SeedUser Owner { get; set; } = default!;
    public SeedUser Reviewer { get; set; } = default!;
    public SeedUser Approver { get; set; } = default!;
    public SeedUser Scheduler { get; set; } = default!;
    public SeedUser Canceller { get; set; } = default!;
    public SeedUser Attendant { get; set; } = default!;

    public string RegistryReference { get; set; } = "";
    public string ApplicantName { get; set; } = "";
    public string Organization { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public RegistryPlan Registry { get; set; }
    /// <summary>Set when this verification is the one recorded with a seeded registry IntegrationAttempt.</summary>
    public Guid? RegistryAttemptId { get; set; }
    public string? RegistryDetail { get; set; }
    public bool Override { get; set; }

    public EventCategory Category { get; set; } = default!;
    public string Title { get; set; } = "";
    public Venue? Venue { get; set; }
    public DateTimeOffset? Start { get; set; }
    public int? Duration { get; set; }
    public int? Attendees { get; set; }
    public int WizardSections { get; set; } = 4;
    public List<(Resource Resource, int Quantity)> Resources { get; } = [];
    public string? CollisionNote { get; set; }

    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Limit { get; set; }
    public int AnsweredRequests { get; set; }
    public RequestPlan OpenRequest { get; set; }
    public string? Reason { get; set; }

    public CalendarPushState Calendar { get; set; } = CalendarPushState.Pushed;
    public ExternalPlan? CalendarConflict { get; set; }
    public List<DateTimeOffset> Alternatives { get; } = [];
    public WeatherPlan Weather { get; set; }
    public bool Revoke { get; set; }
    public bool Reissue { get; set; }
    public bool AssignAttendant { get; set; }
    public int ReadinessDone { get; set; }
    public List<IncidentPlan> Incidents { get; } = [];
    /// <summary>Template codes that get a Notification row, with the delivery state to seed (25 overall).</summary>
    public Dictionary<string, NotificationStatus> Notify { get; } = [];

    public DateTimeOffset? End => Start is { } s && Duration is { } d ? s.AddMinutes(d) : null;
    public CaseStatus FinalStatus => SeedMix.FinalStatus(Path);
}

/// <summary>Occupied windows and resource use while planning, so active reservations never collide (FR-06, FR-07).</summary>
public sealed class SeedTimeline
{
    private readonly Dictionary<Guid, List<(DateTimeOffset Start, DateTimeOffset End)>> _busy = [];
    private readonly List<(Guid Resource, DateTimeOffset Start, DateTimeOffset End, int Quantity)> _allocations = [];
    private readonly HashSet<(Guid Venue, DateOnly Date)> _severe = [];

    public void Block(Guid venueId, DateTimeOffset start, DateTimeOffset end)
    {
        if (!_busy.TryGetValue(venueId, out var list)) _busy[venueId] = list = [];
        list.Add((start, end));
    }

    public bool IsFree(Guid venueId, DateTimeOffset start, DateTimeOffset end, (DateTimeOffset Start, DateTimeOffset End)? ignore = null) =>
        !_busy.TryGetValue(venueId, out var list) || !list.Any(w => w != ignore && ConflictEngine.Overlaps(start, end, w.Start, w.End));

    public void MarkSevere(Guid venueId, DateOnly date) => _severe.Add((venueId, date));

    public bool IsSevere(Guid venueId, DateOnly date) => _severe.Contains((venueId, date));

    public int Available(Resource resource, DateTimeOffset start, DateTimeOffset end) =>
        resource.TotalQuantity - _allocations.Where(a => a.Resource == resource.Id && ConflictEngine.Overlaps(start, end, a.Start, a.End)).Sum(a => a.Quantity);

    public void Allocate(Resource resource, DateTimeOffset start, DateTimeOffset end, int quantity) => _allocations.Add((resource.Id, start, end, quantity));

    public static VenueFacts Facts(Venue venue) => SchedulingService.Facts(venue);
}
