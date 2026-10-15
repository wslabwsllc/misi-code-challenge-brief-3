using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

/// <summary>
/// Guard facts for one case, loaded once. Commands enforce the same rules; the detail page uses these facts to
/// show which actions are possible and, when not, why (E4-04 AC4).
/// </summary>
public sealed class CaseFacts
{
    public int OpenRequests { get; init; }
    public int OverdueRequests { get; init; }
    public RegistryVerification? LatestVerification { get; init; }
    public Reservation? Reservation { get; init; }
    public bool CalendarReconciled { get; init; }
    public string? CalendarBlock { get; init; }
    public int BlockingIncidents { get; init; }
    public int OpenIncidents { get; init; }
    public bool HasValidPermit { get; init; }
    public bool CheckedOut { get; init; }
    public int ReadinessTotal { get; init; }
    public int ReadinessDone { get; init; }
    public IReadOnlySet<string> ReviewerIds { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> AssignedAttendants { get; init; } = new HashSet<string>();
    public bool SevereWeather { get; init; }
    public bool WeatherAcknowledged { get; init; }

    public static async Task<CaseFacts> LoadAsync(AppDbContext db, PermitApplication app, DateTimeOffset now, CancellationToken ct)
    {
        var requests = await db.InformationRequests.AsNoTracking().Where(r => r.ApplicationId == app.Id && r.Status == InformationRequestStatus.Open).ToListAsync(ct);
        var reservation = await db.Reservations.AsNoTracking().Where(r => r.ApplicationId == app.Id && r.Status != ReservationStatus.Released)
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        var (reconciled, block) = reservation is null ? (false, "No reservation yet.") : await CalendarStateAsync(db, reservation, ct);
        var incidents = await db.Incidents.AsNoTracking().Where(i => i.ApplicationId == app.Id).ToListAsync(ct);
        var permit = await db.Permits.AsNoTracking().Include(p => p.Versions).FirstOrDefaultAsync(p => p.ApplicationId == app.Id, ct);
        var checkIn = await db.CheckIns.AsNoTracking().Where(c => c.ApplicationId == app.Id).OrderByDescending(c => c.CheckedInAt).FirstOrDefaultAsync(ct);
        var readiness = await db.ReadinessItems.AsNoTracking().Where(r => r.ApplicationId == app.Id).ToListAsync(ct);
        var reviewers = await db.EligibilityReviews.AsNoTracking().Where(r => r.ApplicationId == app.Id).Select(r => r.ReviewerUserId).Distinct().ToListAsync(ct);
        var attendants = await db.AttendantAssignments.AsNoTracking().Where(a => a.ApplicationId == app.Id && a.Active).Select(a => a.UserId).ToListAsync(ct);
        var severe = await SevereWeatherAsync(db, app.Id, reservation, ct);
        return new CaseFacts
        {
            OpenRequests = requests.Count,
            OverdueRequests = requests.Count(r => r.IsOverdue || r.DueDate < DateOnly.FromDateTime(now.UtcDateTime)),
            LatestVerification = await db.RegistryVerifications.AsNoTracking().Where(v => v.ApplicationId == app.Id).OrderByDescending(v => v.CreatedAt).FirstOrDefaultAsync(ct),
            Reservation = reservation,
            CalendarReconciled = reconciled,
            CalendarBlock = block,
            BlockingIncidents = incidents.Count(i => i.BlocksClosure),
            OpenIncidents = incidents.Count(i => i.Status != IncidentStatus.Resolved),
            HasValidPermit = permit is { Status: PermitStatus.Active } && permit.Versions.Any(v => v.VersionNumber == permit.CurrentVersion && v.Status == PermitVersionStatus.Active),
            CheckedOut = checkIn?.CheckedOutAt is not null,
            ReadinessTotal = readiness.Count,
            ReadinessDone = readiness.Count(r => r.Done),
            ReviewerIds = reviewers.ToHashSet(),
            AssignedAttendants = attendants.ToHashSet(),
            SevereWeather = severe,
            WeatherAcknowledged = app.WeatherAcknowledgedAt is not null,
        };
    }

    /// <summary>
    /// A-18/A-27: final approval needs the hold pushed to the regional calendar, no open reconciliation item, and a
    /// successful sync that started after the push (so the slot is confirmed against current external data).
    /// </summary>
    public static async Task<(bool Reconciled, string? Block)> CalendarStateAsync(AppDbContext db, Reservation reservation, CancellationToken ct)
    {
        if (await db.ReconciliationItems.AsNoTracking().AnyAsync(r => r.ReservationId == reservation.Id && r.Status == ReconciliationStatus.Open, ct))
            return (false, "A regional calendar conflict is waiting in the reconciliation queue.");
        switch (reservation.CalendarState)
        {
            case CalendarPushState.Pushed:
                break;
            case CalendarPushState.Conflict:
                return (false, "The regional calendar reported a conflict for this slot.");
            case CalendarPushState.Failed:
            case CalendarPushState.Pending:
            case CalendarPushState.NotPushed:
                return (false, "The hold has not reached the regional calendar yet (calendar unavailable or push pending).");
        }
        var pushedAt = reservation.CalendarPushedAt ?? DateTimeOffset.MinValue;
        var synced = await db.CalendarSyncRuns.AsNoTracking().AnyAsync(r => r.Succeeded && r.StartedAt >= pushedAt, ct);
        return synced ? (true, null) : (false, "Waiting for a successful calendar sync after the hold was placed.");
    }

    public static async Task<bool> SevereWeatherAsync(AppDbContext db, Guid applicationId, Reservation? reservation, CancellationToken ct)
    {
        if (reservation is null) return false;
        var venue = await db.Venues.AsNoTracking().Include(v => v.Cemetery).FirstOrDefaultAsync(v => v.Id == reservation.VenueId, ct);
        if (venue?.Cemetery is null) return false;
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(reservation.StartUtc, CaseService.Zone(venue.Cemetery.TimeZoneId)).DateTime);
        var latest = await db.WeatherSnapshots.AsNoTracking().Where(w => w.VenueId == venue.Id && w.Date == date).OrderByDescending(w => w.RetrievedAt).FirstOrDefaultAsync(ct);
        return latest?.Advisory == WeatherAdvisory.Severe;
    }

    public IReadOnlyList<string> Flags(WeatherSnapshot? weather)
    {
        var flags = new List<string>();
        if (OverdueRequests > 0) flags.Add($"{OverdueRequests} overdue information request(s)");
        else if (OpenRequests > 0) flags.Add($"{OpenRequests} open information request(s)");
        if (weather?.Advisory == WeatherAdvisory.Severe) flags.Add(WeatherAcknowledged ? "Severe weather (acknowledged)" : "Severe weather advisory — manager acknowledgement required");
        if (Reservation is not null && !CalendarReconciled && CalendarBlock is not null) flags.Add("Calendar: " + CalendarBlock);
        if (BlockingIncidents > 0) flags.Add($"{BlockingIncidents} unresolved High/Critical incident(s)");
        else if (OpenIncidents > 0) flags.Add($"{OpenIncidents} open incident(s)");
        return flags;
    }

    public IReadOnlyList<ActionInfo> Actions(Actor actor, PermitApplication app)
    {
        var list = new List<ActionInfo>();
        foreach (var rule in Workflow.AvailableFrom(app.Status))
        {
            if (!actor.Has(rule.Role)) continue;
            string? blocked = rule.Transition switch
            {
                Transition.Submit or Transition.Resubmit when app.OwnerUserId != actor.UserId => "Only the coordinator who created the application can do this.",
                Transition.Resubmit when OpenRequests > 0 => "Answer every open information request first.",
                Transition.MarkEligible when LatestVerification is null => "Verify the applicant against the registry first.",
                Transition.MarkEligible when LatestVerification.Outcome == VerificationOutcome.Pending => "The registry verification is still pending; retry it.",
                Transition.Reserve when !actor.Can(Cap.ManageReservations) => "Only a Scheduling Officer can reserve.",
                Transition.SubmitForApproval when Reservation is null => "Create the reservation first.",
                Transition.SubmitForApproval when string.IsNullOrWhiteSpace(Reservation.AttendantNeeds) => "Complete the reservation summary (attendant needs).",
                Transition.Approve or Transition.RejectApproval when ReviewerIds.Contains(actor.UserId) => "Separation of duties: you reviewed this case's eligibility.",
                Transition.Approve when !CalendarReconciled => CalendarBlock,
                Transition.IssuePermit when SevereWeather && !WeatherAcknowledged => "Acknowledge the severe weather advisory first.",
                Transition.CheckIn when !AssignedAttendants.Contains(actor.UserId) => "Only the attendant assigned to this event can check it in.",
                Transition.CheckIn when !HasValidPermit => "There is no valid permit version.",
                Transition.Complete when !AssignedAttendants.Contains(actor.UserId) => "Only the assigned attendant can complete the event.",
                Transition.Complete when !CheckedOut => "Record the check-out first.",
                Transition.Complete when ReadinessDone < ReadinessTotal => $"Finish the readiness checklist ({ReadinessDone} of {ReadinessTotal} done).",
                Transition.Close when BlockingIncidents > 0 => $"Resolve {BlockingIncidents} High/Critical incident(s) first.",
                _ => null,
            };
            list.Add(new ActionInfo(rule.Id, rule.Name, blocked is null, blocked));
        }
        return list;
    }
}
