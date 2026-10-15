using EventPermits.Application;

namespace EventPermits.Web.Components;

public sealed record PageEntry(int Number, string Title, string Route, string Capability, string Group, string Purpose);

/// <summary>
/// The 26 mandatory routed pages (brief §2, plan §3.2). Navigation, the route-reachability tests, the remote smoke
/// and the screenshot tool all read this list, so they cannot drift apart.
/// </summary>
public static class PageCatalog
{
    public static readonly IReadOnlyList<PageEntry> All =
    [
        new(1, "Operations Dashboard", "/dashboard", Cap.ViewDashboard, "Overview", "KPIs, today's events, pending actions, incidents and deadlines"),
        new(2, "Application Register", "/applications", Cap.ViewRegister, "Cases", "Search, filter, sort and export the cases you may see"),
        new(3, "New Application", "/applications/new", Cap.CreateApplication, "Cases", "Four-step intake with autosave"),
        new(4, "Application Detail", "/applications/detail", Cap.ViewRegister, "Cases", "Find a case and work on it"),
        new(5, "Eligibility Review Queue", "/reviews", Cap.ReviewEligibility, "Cases", "Review submitted applications"),
        new(6, "Information Requests", "/information-requests", Cap.TrackInformationRequests, "Cases", "Clarifications, due dates and responses"),
        new(23, "Document Vault", "/documents", Cap.ViewDocuments, "Cases", "Attachments, previews and expiring links"),
        new(7, "Event Calendar", "/calendar", Cap.ViewSchedule, "Scheduling", "Month, week and day views"),
        new(8, "Venue Availability", "/venues/availability", Cap.ViewSchedule, "Scheduling", "Opening hours, closures and live collision checks"),
        new(9, "Resource Planner", "/resources", Cap.ViewSchedule, "Scheduling", "Allocate chairs, tents, attendants and AV kits"),
        new(10, "Approval Workspace", "/approvals", Cap.ApproveCases, "Decisions", "Approve or reject with sign-off"),
        new(11, "Permit Center", "/permits", Cap.ViewPermits, "Decisions", "Issue, reissue and revoke permits"),
        new(13, "Daily Event Roster", "/operations/roster", Cap.ViewRoster, "Event day", "Who is where, readiness and run-of-show"),
        new(14, "Event Check-In", "/operations/check-in", Cap.VerifyPermitToken, "Event day", "Verify permits, record arrival and departure"),
        new(15, "Incident Register", "/incidents", Cap.ViewIncidents, "Event day", "Incidents, follow-up and resolution"),
        new(12, "Notification Center", "/notifications", Cap.ViewNotifications, "Communications", "Outbox, delivery status and retries"),
        new(22, "Messaging Delivery Monitor", "/integrations/messaging", Cap.ManageNotifications, "Communications", "Queue, webhook receipts and failures"),
        new(16, "Reports & Exports", "/reports", Cap.ViewReports, "Overview", "Throughput, approval time, utilisation and incidents"),
        new(19, "Integration Hub", "/integrations", Cap.ViewIntegrations, "Integrations", "Health of every external system"),
        new(20, "Registry Verification", "/integrations/registry", Cap.ViewRegistry, "Integrations", "Verify applicant references"),
        new(21, "Regional Calendar Sync", "/integrations/calendar", Cap.SyncCalendar, "Integrations", "OAuth-connected external events and reconciliation"),
        new(24, "Weather & Advisories", "/integrations/weather", Cap.ViewWeather, "Integrations", "Forecasts, cache freshness and severe warnings"),
        new(25, "Data Import", "/integrations/imports", Cap.ManageImports, "Integrations", "Partner CSV ingestion and row errors"),
        new(17, "Venues", "/admin/venues", Cap.ViewVenues, "Administration", "Venues, closures, inventories and categories"),
        new(26, "Venue Map", "/venues/map", Cap.ViewMap, "Administration", "Geocoded venues and coordinate checks"),
        new(18, "Users & Rules", "/admin/users-rules", Cap.ViewAudit, "Administration", "Accounts, roles, rules, templates and audit"),
    ];

    public static readonly IReadOnlyList<string> GroupOrder = ["Overview", "Cases", "Scheduling", "Decisions", "Event day", "Communications", "Integrations", "Administration"];
}
