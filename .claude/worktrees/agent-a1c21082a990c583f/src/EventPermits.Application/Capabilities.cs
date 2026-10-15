using EventPermits.Domain;

namespace EventPermits.Application;

/// <summary>
/// Capability matrix (E1-02). Every page policy and every service command checks one of these.
/// Derived from brief §3 allowed/prohibited columns; docs/ROLE-MATRIX.md is generated from it.
/// </summary>
public static class Cap
{
    public const string ViewDashboard = "ViewDashboard";
    public const string ViewRegister = "ViewRegister";
    public const string ExportRegister = "ExportRegister";
    public const string CreateApplication = "CreateApplication";
    public const string RespondToRequests = "RespondToRequests";
    public const string ReviewEligibility = "ReviewEligibility";
    public const string TrackInformationRequests = "TrackInformationRequests";
    public const string VerifyRegistry = "VerifyRegistry";
    public const string ViewRegistry = "ViewRegistry";
    public const string ViewSchedule = "ViewSchedule";
    public const string ManageReservations = "ManageReservations";
    public const string ApproveCases = "ApproveCases";
    public const string ManagePermits = "ManagePermits";
    public const string ViewPermits = "ViewPermits";
    public const string CancelCases = "CancelCases";
    public const string CloseCases = "CloseCases";
    public const string ViewRoster = "ViewRoster";
    public const string AssignAttendants = "AssignAttendants";
    public const string RecordCheckIn = "RecordCheckIn";
    public const string VerifyPermitToken = "VerifyPermitToken";
    public const string ReportIncidents = "ReportIncidents";
    public const string ManageIncidents = "ManageIncidents";
    public const string ViewIncidents = "ViewIncidents";
    public const string Comment = "Comment";
    public const string DownloadDocuments = "DownloadDocuments";
    public const string ViewDocuments = "ViewDocuments";
    public const string ViewNotifications = "ViewNotifications";
    public const string ManageNotifications = "ManageNotifications";
    public const string ViewReports = "ViewReports";
    public const string ViewAudit = "ViewAudit";
    public const string ExportAudit = "ExportAudit";
    public const string ManageUsers = "ManageUsers";
    public const string ManageRules = "ManageRules";
    public const string ViewVenues = "ViewVenues";
    public const string ManageVenues = "ManageVenues";
    public const string ViewIntegrations = "ViewIntegrations";
    public const string ManageIntegrations = "ManageIntegrations";
    public const string SyncCalendar = "SyncCalendar";
    public const string ViewWeather = "ViewWeather";
    public const string AcknowledgeWeather = "AcknowledgeWeather";
    public const string ManageImports = "ManageImports";
    public const string ConfirmGeocode = "ConfirmGeocode";
    public const string ViewMap = "ViewMap";

    private const string I = Roles.IntakeCoordinator, R = Roles.EligibilityReviewer, S = Roles.SchedulingOfficer,
        O = Roles.OperationsManager, A = Roles.EventAttendant, C = Roles.ComplianceAuditor, X = Roles.SystemAdmin;

    public static readonly IReadOnlyDictionary<string, string[]> Matrix = new Dictionary<string, string[]>
    {
        [ViewDashboard] = [I, R, S, O, A, C, X],
        [ViewRegister] = [I, R, S, O, C],
        [ExportRegister] = [I, R, S, O, C],
        [CreateApplication] = [I],
        [RespondToRequests] = [I],
        [ReviewEligibility] = [R],
        [TrackInformationRequests] = [I, R],
        [VerifyRegistry] = [R, X],
        [ViewRegistry] = [R, X, C],
        [ViewSchedule] = [R, S, O],
        [ManageReservations] = [S],
        [ApproveCases] = [O],
        [ManagePermits] = [O],
        [ViewPermits] = [O, C],
        [CancelCases] = [O],
        [CloseCases] = [O],
        [ViewRoster] = [S, O, A],
        [AssignAttendants] = [O],
        [RecordCheckIn] = [A],
        [VerifyPermitToken] = [A, O],
        [ReportIncidents] = [A, O],
        [ManageIncidents] = [O],
        [ViewIncidents] = [A, O, C],
        [Comment] = [I, R, S, O],
        [DownloadDocuments] = [I, R, S, O],
        [ViewDocuments] = [I, R, S, O, C],
        [ViewNotifications] = [O, X, C],
        [ManageNotifications] = [O, X],
        [ViewReports] = [O, C],
        [ViewAudit] = [X, C],
        [ExportAudit] = [C],
        [ManageUsers] = [X],
        [ManageRules] = [X],
        [ViewVenues] = [X, S],
        [ManageVenues] = [X],
        [ViewIntegrations] = [X, O],
        [ManageIntegrations] = [X],
        [SyncCalendar] = [S, O, X],
        [ViewWeather] = [O, S, X],
        [AcknowledgeWeather] = [O],
        [ManageImports] = [X, S],
        [ConfirmGeocode] = [X],
        [ViewMap] = [X, S, O],
    };

    public static bool Allows(string capability, IEnumerable<string> roles) =>
        Matrix.TryGetValue(capability, out var allowed) && roles.Any(allowed.Contains);

    /// <summary>Markdown table for docs/ROLE-MATRIX.md (a test keeps the file in sync).</summary>
    public static string ToMarkdown()
    {
        var lines = new List<string>
        {
            "| Capability | " + string.Join(" | ", Roles.All.Select(Roles.Display)) + " |",
            "| --- | " + string.Join(" | ", Roles.All.Select(_ => "---")) + " |",
        };
        foreach (var (capability, allowed) in Matrix.OrderBy(m => m.Key))
            lines.Add($"| {capability} | " + string.Join(" | ", Roles.All.Select(r => allowed.Contains(r) ? "yes" : "")) + " |");
        return string.Join('\n', lines);
    }
}
