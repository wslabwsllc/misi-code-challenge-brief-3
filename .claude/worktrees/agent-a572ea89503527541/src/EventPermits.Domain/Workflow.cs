namespace EventPermits.Domain;

/// <summary>The fourteen permitted moves (brief §5, plan §3.4). Nothing else may change a case status.</summary>
public enum Transition
{
    Submit,              // T1
    RequestInformation,  // T2
    Resubmit,            // T3
    MarkEligible,        // T4
    RejectEligibility,   // T5
    Reserve,             // T6
    SubmitForApproval,   // T7
    Approve,             // T8
    RejectApproval,      // T9
    IssuePermit,         // T10
    CheckIn,             // T11
    Complete,            // T12
    Close,               // T13
    Cancel,              // T14
}

public sealed record TransitionRule(Transition Transition, string Id, string Name, IReadOnlyList<CaseStatus> From, CaseStatus To, string Role, string Guards);

public static class Workflow
{
    public static readonly IReadOnlyList<TransitionRule> Rules =
    [
        new(Transition.Submit, "T1", "Submit application", [CaseStatus.Draft], CaseStatus.Submitted, Roles.IntakeCoordinator,
            "Owner only; all wizard sections valid; required documents attached; event in the future; 30-300 minutes; attendees 1..capacity; applicant name and contact"),
        new(Transition.RequestInformation, "T2", "Request information", [CaseStatus.Submitted], CaseStatus.NeedsInformation, Roles.EligibilityReviewer,
            "Missing items listed; future due date"),
        new(Transition.Resubmit, "T3", "Respond and resubmit", [CaseStatus.NeedsInformation], CaseStatus.Submitted, Roles.IntakeCoordinator,
            "Owner only; every open request answered; earlier requests preserved"),
        new(Transition.MarkEligible, "T4", "Pass eligibility", [CaseStatus.Submitted], CaseStatus.Eligible, Roles.EligibilityReviewer,
            "Checklist complete; registry evidence Valid or a permitted override"),
        new(Transition.RejectEligibility, "T5", "Reject at eligibility", [CaseStatus.Submitted], CaseStatus.Rejected, Roles.EligibilityReviewer,
            "Reason required"),
        new(Transition.Reserve, "T6", "Tentatively reserve", [CaseStatus.Eligible], CaseStatus.TentativelyReserved, Roles.SchedulingOfficer,
            "No collision incl. buffers, blackouts, hours, external and partner events; capacity; resources within inventory"),
        new(Transition.SubmitForApproval, "T7", "Submit for approval", [CaseStatus.TentativelyReserved], CaseStatus.PendingApproval, Roles.SchedulingOfficer,
            "Reservation summary complete"),
        new(Transition.Approve, "T8", "Approve", [CaseStatus.PendingApproval], CaseStatus.Approved, Roles.OperationsManager,
            "Approver did not review the case; external calendar reconciled"),
        new(Transition.RejectApproval, "T9", "Reject at approval", [CaseStatus.PendingApproval], CaseStatus.Rejected, Roles.OperationsManager,
            "Reason required; approver did not review the case; holds released"),
        new(Transition.IssuePermit, "T10", "Issue permit", [CaseStatus.Approved], CaseStatus.Permitted, Roles.OperationsManager,
            "Immutable permit version; issuer recorded; severe weather acknowledged"),
        new(Transition.CheckIn, "T11", "Check in", [CaseStatus.Permitted], CaseStatus.CheckedIn, Roles.EventAttendant,
            "Assigned attendant; valid token inside the check-in window"),
        new(Transition.Complete, "T12", "Complete event", [CaseStatus.CheckedIn], CaseStatus.EventCompleted, Roles.EventAttendant,
            "Assigned attendant; check-out recorded; readiness checklist complete"),
        new(Transition.Close, "T13", "Close case", [CaseStatus.EventCompleted], CaseStatus.Closed, Roles.OperationsManager,
            "No unresolved High or Critical incident"),
        new(Transition.Cancel, "T14", "Cancel case",
            [CaseStatus.Submitted, CaseStatus.Eligible, CaseStatus.TentativelyReserved, CaseStatus.PendingApproval, CaseStatus.Approved, CaseStatus.Permitted],
            CaseStatus.Cancelled, Roles.OperationsManager,
            "Reason required; reservations and allocations released; permit invalidated"),
    ];

    public static TransitionRule Rule(Transition transition) => Rules.Single(r => r.Transition == transition);

    public static bool IsAllowed(Transition transition, CaseStatus from) => Rule(transition).From.Contains(from);

    /// <summary>True when any rule moves <paramref name="from"/> to <paramref name="to"/>; used by tests and the state diagram.</summary>
    public static bool Exists(CaseStatus from, CaseStatus to) => Rules.Any(r => r.To == to && r.From.Contains(from));

    public static IEnumerable<TransitionRule> AvailableFrom(CaseStatus status) => Rules.Where(r => r.From.Contains(status));

    public static string Display(CaseStatus status) => status switch
    {
        CaseStatus.NeedsInformation => "Needs Information",
        CaseStatus.TentativelyReserved => "Tentatively Reserved",
        CaseStatus.PendingApproval => "Pending Approval",
        CaseStatus.CheckedIn => "Checked In",
        CaseStatus.EventCompleted => "Event Completed",
        _ => status.ToString(),
    };

    /// <summary>Statuses where the case is finished and no further business edits are allowed.</summary>
    public static bool IsTerminal(CaseStatus status) => status is CaseStatus.Closed or CaseStatus.Rejected or CaseStatus.Cancelled;

    /// <summary>Mermaid state diagram generated from <see cref="Rules"/> (docs/WORKFLOW.md is checked against it).</summary>
    public static string ToMermaid()
    {
        var lines = new List<string> { "stateDiagram-v2", "    [*] --> Draft" };
        foreach (var rule in Rules)
            foreach (var from in rule.From)
                lines.Add($"    {from} --> {rule.To} : {rule.Id} {rule.Name}");
        lines.Add("    Closed --> [*]");
        lines.Add("    Rejected --> [*]");
        lines.Add("    Cancelled --> [*]");
        return string.Join('\n', lines);
    }
}
