using System.Globalization;
using System.Text;
using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public sealed record Kpi(string Key, string Label, int Value, string Href, string Tone);
public sealed record TodayEvent(Guid Id, string Reference, string Title, string Venue, DateTimeOffset StartUtc, string TimeZoneId, CaseStatus Status);
public sealed record PendingAction(Guid Id, string Reference, string Action, string Href, DateTimeOffset Since);
public sealed record DeadlineItem(string Kind, string Label, DateOnly Due, string Href, bool Overdue);
public sealed record DashboardView(IReadOnlyList<Kpi> Kpis, IReadOnlyList<TodayEvent> Today, IReadOnlyList<PendingAction> Pending, IReadOnlyList<IncidentRow> Incidents,
    IReadOnlyList<DeadlineItem> Deadlines, IReadOnlyList<IntegrationCard> Integrations);

public sealed record ReportFilter(DateOnly From, DateOnly To, Guid? CemeteryId);
public sealed record Bar(string Label, double Value, string Display, string? Href);
public sealed record ReportView(ReportFilter Filter, IReadOnlyList<Bar> ThroughputByStatus, IReadOnlyList<Bar> SubmissionsByWeek, IReadOnlyList<Bar> ApprovalDays,
    double? MedianApprovalDays, double? P90ApprovalDays, IReadOnlyList<Bar> VenueUtilisation, IReadOnlyList<Bar> IncidentsBySeverity, int Applications);

/// <summary>Operations Dashboard (E11-01) and Reports &amp; Exports (E11-02): every number is a database query in the caller's scope.</summary>
public sealed partial class DashboardService(AppDb dbs, ICorrelationContext correlation, IntegrationHubService hub) : ServiceBase(dbs, correlation)
{
    public async Task<DashboardView> GetAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewDashboard);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var scoped = Scoped(db, actor).AsNoTracking();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        var kpis = new List<Kpi>();
        if (actor.Can(Cap.ViewRegister))
        {
            kpis.Add(new("open", "Open cases", await scoped.CountAsync(a => !(a.Status == CaseStatus.Closed || a.Status == CaseStatus.Rejected || a.Status == CaseStatus.Cancelled || a.Status == CaseStatus.Draft), ct),
                "/applications?status=Submitted,NeedsInformation,Eligible,TentativelyReserved,PendingApproval,Approved,Permitted,CheckedIn,EventCompleted", "brand"));
            kpis.Add(new("submitted", "Awaiting eligibility review", await scoped.CountAsync(a => a.Status == CaseStatus.Submitted, ct), "/applications?status=Submitted", "brand"));
            kpis.Add(new("pending-approval", "Pending approval", await scoped.CountAsync(a => a.Status == CaseStatus.PendingApproval, ct), "/applications?status=PendingApproval", "warn"));
        }
        if (actor.Has(Roles.IntakeCoordinator))
            kpis.Add(new("drafts", "My drafts", await scoped.CountAsync(a => a.Status == CaseStatus.Draft && a.OwnerUserId == actor.UserId, ct), "/applications?myDrafts=true", "brand"));
        if (actor.Can(Cap.TrackInformationRequests))
            kpis.Add(new("overdue-requests", "Overdue information requests", await db.InformationRequests.AsNoTracking()
                .CountAsync(r => scoped.Select(a => a.Id).Contains(r.ApplicationId) && r.Status == InformationRequestStatus.Open && r.DueDate < today, ct), "/information-requests?view=Overdue", "danger"));
        if (actor.Can(Cap.ViewIncidents))
            kpis.Add(new("high-incidents", "Open High/Critical incidents", await db.Incidents.AsNoTracking()
                .CountAsync(i => scoped.Select(a => a.Id).Contains(i.ApplicationId) && i.Status != IncidentStatus.Resolved && i.Severity >= IncidentSeverity.High, ct), "/incidents?status=Open&severity=High", "danger"));
        if (actor.Can(Cap.ViewNotifications))
            kpis.Add(new("failed-notifications", "Failed notifications", await db.Notifications.AsNoTracking().CountAsync(n => n.Status == NotificationStatus.Failed, ct), "/notifications?status=Failed", "warn"));

        var today_ = new List<TodayEvent>();
        var cemeteries = await db.Cemeteries.AsNoTracking().ToListAsync(ct);
        foreach (var c in cemeteries)
        {
            var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, CaseService.Zone(c.TimeZoneId)).DateTime);
            var start = CaseService.ToUtc(localToday, TimeOnly.MinValue, c.TimeZoneId);
            today_.AddRange(await scoped.Where(a => a.Status != CaseStatus.Cancelled && a.Status != CaseStatus.Rejected)
                .SelectMany(a => a.Reservations.Where(r => r.Status != ReservationStatus.Released && r.Venue!.CemeteryId == c.Id && r.StartUtc >= start && r.StartUtc < start.AddDays(1)),
                    (a, r) => new TodayEvent(a.Id, a.Reference, a.Event!.Title, r.Venue!.Name, r.StartUtc, c.TimeZoneId, a.Status)).ToListAsync(ct));
        }
        if (actor.Can(Cap.ViewRoster)) kpis.Insert(0, new("today", "Events today", today_.Count, $"/operations/roster?date={today:yyyy-MM-dd}", "ok"));

        var pending = new List<PendingAction>();
        async Task Add(string capability, CaseStatus status, string action, Func<Guid, string> href)
        {
            if (!actor.Can(capability)) return;
            pending.AddRange((await scoped.Where(a => a.Status == status).OrderBy(a => a.StatusChangedAt).Take(5)
                .Select(a => new { a.Id, a.Reference, a.StatusChangedAt }).ToListAsync(ct))
                .Select(a => new PendingAction(a.Id, a.Reference, action, href(a.Id), a.StatusChangedAt)));
        }
        await Add(Cap.ReviewEligibility, CaseStatus.Submitted, "Review eligibility", id => $"/reviews/{id}");
        await Add(Cap.ManageReservations, CaseStatus.Eligible, "Reserve a venue", id => $"/venues/availability?case={id}");
        await Add(Cap.ApproveCases, CaseStatus.PendingApproval, "Approve or reject", _ => "/approvals");
        await Add(Cap.ManagePermits, CaseStatus.Approved, "Issue the permit", _ => "/permits");
        await Add(Cap.CloseCases, CaseStatus.EventCompleted, "Close the case", id => $"/applications/{id}");
        await Add(Cap.RespondToRequests, CaseStatus.NeedsInformation, "Answer the information request", _ => "/information-requests");

        IReadOnlyList<IncidentRow> incidents = [];
        if (actor.Can(Cap.ViewIncidents))
        {
            incidents = (await db.Incidents.AsNoTracking().Where(i => scoped.Select(a => a.Id).Contains(i.ApplicationId) && i.Status != IncidentStatus.Resolved)
                .OrderByDescending(i => i.Severity).ThenByDescending(i => i.CreatedAt).Take(5)
                .Join(db.Applications, i => i.ApplicationId, a => a.Id, (i, a) => new { i, a.Reference, a.Event!.Title }).ToListAsync(ct))
                .Select(x => new IncidentRow(x.i.Id, x.i.Reference, x.i.ApplicationId, x.Reference, x.Title, x.i.Category, x.i.Severity, x.i.Status, x.i.Description, x.i.ReportedByName,
                    x.i.OwnerName, x.i.DueDate, x.i.DueDate < today, x.i.CreatedAt, 0, x.i.Version)).ToList();
        }

        var deadlines = new List<DeadlineItem>();
        if (actor.Can(Cap.TrackInformationRequests))
            deadlines.AddRange((await db.InformationRequests.AsNoTracking().Where(r => scoped.Select(a => a.Id).Contains(r.ApplicationId) && r.Status == InformationRequestStatus.Open)
                .OrderBy(r => r.DueDate).Take(5).Join(db.Applications, r => r.ApplicationId, a => a.Id, (r, a) => new { r.DueDate, a.Reference, a.Id }).ToListAsync(ct))
                .Select(x => new DeadlineItem("Information request", $"{x.Reference} response due", x.DueDate, "/information-requests", x.DueDate < today)));
        if (actor.Can(Cap.ViewIncidents))
            deadlines.AddRange((await db.Incidents.AsNoTracking().Where(i => scoped.Select(a => a.Id).Contains(i.ApplicationId) && i.Status != IncidentStatus.Resolved && i.DueDate != null)
                .OrderBy(i => i.DueDate).Take(5).Select(i => new { i.Reference, i.DueDate, i.Id }).ToListAsync(ct))
                .Select(x => new DeadlineItem("Incident follow-up", $"{x.Reference} follow-up due", x.DueDate!.Value, $"/incidents/{x.Id}", x.DueDate < today)));

        IReadOnlyList<IntegrationCard> cards = actor.Can(Cap.ViewIntegrations) ? (await hub.CardsAsync(actor, ct)).Where(c => c.Health is IntegrationHealth.Degraded or IntegrationHealth.Offline).ToList() : [];
        if (actor.Can(Cap.ViewIntegrations)) kpis.Add(new("degraded", "Degraded integrations", cards.Count, "/integrations", cards.Count > 0 ? "warn" : "ok"));
        return new DashboardView(kpis, today_.OrderBy(t => t.StartUtc).ToList(), pending.OrderBy(p => p.Since).Take(12).ToList(), incidents, deadlines.OrderBy(d => d.Due).ToList(), cards);
    }

    public async Task<ReportView> ReportAsync(Actor actor, ReportFilter filter, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewReports);
        if (filter.To < filter.From) throw ValidationException.For("to", "The end date must not be before the start date.");
        if (filter.To.DayNumber - filter.From.DayNumber > 731) throw ValidationException.For("to", "Choose a range of at most two years.");
        await using var db = await Dbs.CreateAsync(actor, ct);
        var from = new DateTimeOffset(filter.From.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = new DateTimeOffset(filter.To.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var apps = Scoped(db, actor).AsNoTracking().Where(a => a.CreatedAt >= from && a.CreatedAt < to);
        if (filter.CemeteryId is { } cid) apps = apps.Where(a => a.Event!.CemeteryId == cid);
        var dateQuery = $"from={filter.From:yyyy-MM-dd}&to={filter.To:yyyy-MM-dd}" + (filter.CemeteryId is { } c ? $"&cemeteryId={c}" : "");

        var byStatus = await apps.GroupBy(a => a.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var throughput = Enum.GetValues<CaseStatus>().Select(s => new Bar(Workflow.Display(s), byStatus.FirstOrDefault(b => b.Key == s)?.Count ?? 0,
            (byStatus.FirstOrDefault(b => b.Key == s)?.Count ?? 0).ToString(CultureInfo.InvariantCulture), $"/applications?status={s}&{dateQuery}")).ToList();

        var submitted = await apps.Where(a => a.SubmittedAt != null).Select(a => a.SubmittedAt!.Value).ToListAsync(ct);
        var weeks = submitted.GroupBy(d => ISOWeek.GetYear(d.UtcDateTime) * 100 + ISOWeek.GetWeekOfYear(d.UtcDateTime)).OrderBy(g => g.Key)
            .Select(g => new Bar($"{g.Key / 100}-W{g.Key % 100:D2}", g.Count(), g.Count().ToString(CultureInfo.InvariantCulture), null)).ToList();

        var decisions = await (from d in db.ApprovalDecisions.AsNoTracking()
                               join a in apps on d.ApplicationId equals a.Id
                               where a.SubmittedAt != null
                               select new { d.DecidedAt, Submitted = a.SubmittedAt!.Value, a.Reference }).ToListAsync(ct);
        var durations = decisions.Select(d => (d.DecidedAt - d.Submitted).TotalDays).OrderBy(d => d).ToList();
        var buckets = new[] { (0.0, 2.0, "0–2 days"), (2.0, 5.0, "2–5 days"), (5.0, 10.0, "5–10 days"), (10.0, 20.0, "10–20 days"), (20.0, double.MaxValue, "20+ days") };
        var approvalBars = buckets.Select(b => { var n = durations.Count(d => d >= b.Item1 && d < b.Item2); return new Bar(b.Item3, n, n.ToString(CultureInfo.InvariantCulture), null); }).ToList();

        var venues = await db.Venues.AsNoTracking().Include(v => v.Cemetery).Where(v => filter.CemeteryId == null || v.CemeteryId == filter.CemeteryId).ToListAsync(ct);
        var reservations = await db.Reservations.AsNoTracking().Where(r => r.Status != ReservationStatus.Released && r.StartUtc < to && r.EndUtc > from).ToListAsync(ct);
        var days = filter.To.DayNumber - filter.From.DayNumber + 1;
        var utilisation = venues.Select(v =>
        {
            // Booked hours (with buffers) over open hours in the range.
            var openHours = Enumerable.Range(0, days).Select(i => filter.From.AddDays(i))
                .Sum(d => v.OpeningHours.Where(h => h.Day == d.DayOfWeek).Sum(h => (h.Closes - h.Opens).TotalHours));
            var booked = reservations.Where(r => r.VenueId == v.Id).Sum(r => (r.BlockEndUtc - r.BlockStartUtc).TotalHours);
            var pct = openHours <= 0 ? 0 : Math.Min(100, 100 * booked / openHours);
            return new Bar(v.Name, Math.Round(pct, 1), $"{pct:0.0}%", $"/calendar?venueId={v.Id}&view=month&date={filter.From:yyyy-MM-dd}");
        }).OrderByDescending(b => b.Value).ToList();

        var incidents = await db.Incidents.AsNoTracking().Where(i => i.CreatedAt >= from && i.CreatedAt < to && Scoped(db, actor).Select(a => a.Id).Contains(i.ApplicationId))
            .GroupBy(i => i.Severity).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var incidentBars = Enum.GetValues<IncidentSeverity>().Select(s => new Bar(s.ToString(), incidents.FirstOrDefault(i => i.Key == s)?.Count ?? 0,
            (incidents.FirstOrDefault(i => i.Key == s)?.Count ?? 0).ToString(CultureInfo.InvariantCulture), $"/incidents?severity={s}&from={filter.From:yyyy-MM-dd}&to={filter.To:yyyy-MM-dd}")).ToList();

        return new ReportView(filter, throughput, weeks, approvalBars, Percentile(durations, 0.5), Percentile(durations, 0.9), utilisation, incidentBars, await apps.CountAsync(ct));
    }

    public async Task<byte[]> ReportCsvAsync(Actor actor, ReportFilter filter, string report, CancellationToken ct = default)
    {
        var view = await ReportAsync(actor, filter, ct);
        var bars = report switch
        {
            "throughput" => view.ThroughputByStatus,
            "submissions" => view.SubmissionsByWeek,
            "approval-time" => view.ApprovalDays,
            "utilisation" => view.VenueUtilisation,
            "incidents" => view.IncidentsBySeverity,
            _ => throw ValidationException.For("report", "Unknown report."),
        };
        var sb = new StringBuilder($"report,{report},from,{filter.From:yyyy-MM-dd},to,{filter.To:yyyy-MM-dd}\nlabel,value\n");
        foreach (var b in bars) sb.AppendLine(Csv.Line([b.Label, b.Value.ToString(CultureInfo.InvariantCulture)]));
        await using var db = await Dbs.CreateAsync(actor, ct);
        AuditLog(db, actor, "Report.Exported", "Report", report, $"Report {report} exported ({filter.From:yyyy-MM-dd} to {filter.To:yyyy-MM-dd})");
        await db.SaveChangesAsync(ct);
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public static double? Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return null;
        var index = (sorted.Count - 1) * p;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        return Math.Round(sorted[lower] + (sorted[upper] - sorted[lower]) * (index - lower), 1);
    }
}
