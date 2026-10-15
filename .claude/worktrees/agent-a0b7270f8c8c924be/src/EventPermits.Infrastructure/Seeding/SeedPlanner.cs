using System.Globalization;
using System.Text;
using EventPermits.Application.Services;
using EventPermits.Domain;

namespace EventPermits.Infrastructure.Seeding;

/// <summary>Reference rows and fixtures the case seed is planned against, plus the anchor instant (A-29).</summary>
public sealed class SeedWorld
{
    public DateTimeOffset Anchor { get; init; }
    public DateOnly AnchorUtcDate => DateOnly.FromDateTime(Anchor.UtcDateTime);
    public Dictionary<string, SeedUser> Users { get; init; } = [];
    public SeedUser System { get; } = new("system", "system", "System", new HashSet<string>());
    public List<Venue> Venues { get; init; } = [];
    public List<EventCategory> Categories { get; init; } = [];
    public Dictionary<Guid, List<Resource>> Resources { get; init; } = [];
    public Dictionary<string, NotificationTemplate> Templates { get; init; } = [];
    public ReferenceData.RegistryFile Registry { get; init; } = default!;
    public ReferenceData.CalendarFile Calendar { get; init; } = default!;
    public ReferenceData.WeatherFile Weather { get; init; } = default!;
    public int CheckInOpensMinutes { get; init; } = 60;
    public int TokenGraceHours { get; init; } = 2;

    public SeedUser U(string userName) => Users[userName];
    public EventCategory Category(string code) => Categories.First(c => c.Code == code);
    public static TimeZoneInfo Zone(Venue v) => CaseService.Zone(v.Cemetery?.TimeZoneId);
    public DateOnly LocalToday(Venue v) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Anchor, Zone(v)).DateTime);
    public static DateOnly LocalDate(Venue v, DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, Zone(v)).DateTime);
    public static DateTimeOffset Local(Venue v, DateOnly date, TimeOnly time) => CaseService.ToUtc(date, time, v.Cemetery?.TimeZoneId ?? "UTC");
}

public sealed class SeedPlan
{
    public SeedTimeline Timeline { get; } = new();
    public List<ExternalPlan> External { get; } = [];
    public List<BlackoutPlan> Blackouts { get; } = [];
    public List<PartnerBatchPlan> PartnerBatches { get; } = [];
    public List<CaseSpec> Cases { get; } = [];
}

/// <summary>
/// Plans the whole demo dataset deterministically from the anchor and a fixed random seed: who does what, when and
/// where. Active reservations are placed through the same rules as the conflict engine (opening hours, buffers,
/// blackouts, external and partner events, inventory); deliberate collisions are only ever proposed slots or
/// released reservations, plus one regional-calendar conflict waiting in the reconciliation queue.
/// </summary>
public sealed class SeedPlanner(SeedWorld w)
{
    public const int RandomSeed = 20261015;
    public const string PartnerHeader = "partnerEventId,siteCode,startUtc,endUtc,attendeeCount,updatedAtUtc";

    private readonly Random _rnd = new(RandomSeed);
    private readonly SeedPlan _plan = new();
    private int _valid, _unknown, _flagged, _missing;

    public SeedPlan Build()
    {
        PlanExternalEvents();
        PlanPartnerFeed();
        PlanBlackouts();
        foreach (var wv in w.Weather.Venues)
            if (w.Venues.FirstOrDefault(v => v.Code == wv.VenueCode) is { } venue)
                foreach (var d in wv.SevereDays) _plan.Timeline.MarkSevere(venue.Id, w.LocalToday(venue).AddDays(d));

        var no = 0;
        foreach (var (path, count) in SeedMix.Counts)
            for (var i = 0; i < count; i++) _plan.Cases.Add(NewSpec(++no, path, i));

        PlaceEventDay();
        PlaceWeatherCases();
        PlaceCalendarConflict();
        PlaceRemainingActive();
        PlaceReleased();
        PlaceProposed();
        foreach (var s in _plan.Cases) Time(s);
        PickNotifications();
        return _plan;
    }

    private List<CaseSpec> Of(SeedPath path) => _plan.Cases.Where(c => c.Path == path).ToList();

    // ------------------------------------------------------------------ environment

    private void PlanExternalEvents()
    {
        foreach (var e in w.Calendar.Events.OrderBy(e => e.ExternalId, StringComparer.Ordinal))
        {
            var venue = w.Venues.FirstOrDefault(v => v.Code == e.VenueCode);
            if (venue is null) continue;
            var start = SeedWorld.Local(venue, w.AnchorUtcDate.AddDays(e.DayOffset), TimeOnly.ParseExact(e.LocalStart, "HH:mm", CultureInfo.InvariantCulture));
            var plan = new ExternalPlan(e.ExternalId, venue, e.Title, start, start.AddMinutes(e.DurationMinutes));
            _plan.External.Add(plan);
            _plan.Timeline.Block(venue.Id, plan.Start, plan.End);
        }
    }

    private void PlanPartnerFeed()
    {
        var clean = new (string Venue, int Day, int Hour, int Minutes, int Attendees)[]
        {
            ("RMP-PAVILION", 3, 10, 120, 90), ("CHG-LAWN", 5, 11, 90, 150), ("MVR-CHAPEL", 8, 9, 60, 40), ("RMP-LAWN", 10, 13, 120, 200), ("CHG-GARDEN", 12, 10, 60, 25),
        };
        var good = new (string Venue, int Day, int Hour, int Minutes, int Attendees)[]
        {
            ("MVR-PAVILION", 6, 14, 90, 80), ("RMP-GARDEN", 9, 11, 60, 30), ("CHG-CHAPEL", 14, 15, 60, 50), ("MVR-LAWN", 16, 10, 120, 180),
        };
        var t1 = Trim(w.Anchor.AddDays(-3).AddHours(-20));
        var t2 = Trim(w.Anchor.AddDays(-2).AddHours(-20));
        var t3 = Trim(w.Anchor.AddDays(-1).AddHours(-20));
        string Row(string id, string site, DateTimeOffset s, DateTimeOffset e, string attendees, DateTimeOffset updated) =>
            $"{id},{site},{Iso(s)},{Iso(e)},{attendees},{Iso(updated)}";
        (string Site, DateTimeOffset Start, DateTimeOffset End) Slot((string Venue, int Day, int Hour, int Minutes, int Attendees) r)
        {
            var venue = w.Venues.First(v => v.Code == r.Venue);
            var start = SeedWorld.Local(venue, w.LocalToday(venue).AddDays(r.Day), new TimeOnly(r.Hour, 0));
            _plan.Timeline.Block(venue.Id, start, start.AddMinutes(r.Minutes));
            return (venue.Code, start, start.AddMinutes(r.Minutes));
        }

        var sb = new StringBuilder(PartnerHeader).Append('\n');
        for (var i = 0; i < clean.Length; i++)
        {
            var (site, s, e) = Slot(clean[i]);
            sb.Append(Row($"PRT-S{i + 1:000}", site, s, e, clean[i].Attendees.ToString(CultureInfo.InvariantCulture), t1)).Append('\n');
        }
        var cleanBytes = Encoding.UTF8.GetBytes(sb.ToString());

        var bad = new StringBuilder(PartnerHeader).Append('\n');
        for (var i = 0; i < good.Length; i++)
        {
            var (site, s, e) = Slot(good[i]);
            bad.Append(Row($"PRT-S{101 + i:000}", site, s, e, good[i].Attendees.ToString(CultureInfo.InvariantCulture), t2)).Append('\n');
        }
        var lawn = w.Venues.First(v => v.Code == "RMP-LAWN");
        var bs = SeedWorld.Local(lawn, w.LocalToday(lawn).AddDays(18), new TimeOnly(12, 0));
        bad.Append(Row("PRT-S105", "RMP-LAWN", bs.AddHours(2), bs, "40", t2)).Append('\n'); // end before start
        bad.Append(Row("PRT-S106", "ZZZ-NOPE", bs, bs.AddHours(1), "30", t2)).Append('\n'); // unknown site
        bad.Append(Row("PRT-S107", "RMP-LAWN", bs, bs.AddHours(1), "many", t2)).Append('\n'); // attendees not a number

        _plan.PartnerBatches.Add(new($"PB-{t1:yyyyMMddHHmmss}-seed01", $"partner-{t1:yyyyMMddHHmmss}-clean.csv", cleanBytes, t1, false));
        _plan.PartnerBatches.Add(new($"PB-{t2:yyyyMMddHHmmss}-seed02", $"partner-{t2:yyyyMMddHHmmss}-bad_rows.csv", Encoding.UTF8.GetBytes(bad.ToString()), t2, false));
        _plan.PartnerBatches.Add(new($"PB-{t3:yyyyMMddHHmmss}-seed03", $"partner-{t3:yyyyMMddHHmmss}-duplicate.csv", cleanBytes, t3, true));
    }

    private void PlanBlackouts()
    {
        string[] reasons =
        [
            "Grounds maintenance", "Irrigation repairs", "Headstone restoration works", "Path resurfacing", "Tree surgery near the venue",
            "Drainage works", "Annual safety inspection", "Private state ceremony (fictional)", "Chapel roof repairs", "Lawn reseeding",
        ];
        var defs = new (string Venue, int Day, int? Hour, int Hours)[]
        {
            ("RMP-CHAPEL", -12, null, 0), ("CHG-LAWN", -6, 8, 4), ("MVR-GARDEN", -2, 13, 3), ("RMP-LAWN", 2, 8, 3), ("CHG-PAVILION", 4, null, 0),
            ("MVR-CHAPEL", 6, 12, 4), ("RMP-GARDEN", 8, 8, 5), ("CHG-CHAPEL", 9, null, 0), ("MVR-LAWN", 11, 14, 4), ("RMP-PAVILION", 13, 8, 4),
            ("CHG-GARDEN", 15, null, 0), ("MVR-PAVILION", 17, 10, 3), ("RMP-CHAPEL", 19, 13, 5), ("CHG-LAWN", 21, null, 0), ("MVR-GARDEN", 24, 8, 6),
            ("RMP-LAWN", 27, 12, 3), ("CHG-PAVILION", 30, 8, 4), ("MVR-CHAPEL", 34, null, 0), ("RMP-GARDEN", 38, 9, 4), ("CHG-CHAPEL", 44, 13, 4),
        };
        for (var i = 0; i < defs.Length; i++)
        {
            var d = defs[i];
            var venue = w.Venues.First(v => v.Code == d.Venue);
            var date = w.LocalToday(venue).AddDays(d.Day);
            var start = SeedWorld.Local(venue, date, new TimeOnly(d.Hour ?? 6, 0));
            var end = d.Hour is null ? SeedWorld.Local(venue, date, new TimeOnly(20, 0)) : start.AddHours(d.Hours);
            var reason = reasons[i % reasons.Length] + (d.Hour is null ? " (venue closed all day)" : "");
            _plan.Blackouts.Add(new BlackoutPlan(venue, start, end, reason));
            _plan.Timeline.Block(venue.Id, start, end);
        }
    }

    // ------------------------------------------------------------------ people, applicants and content

    private CaseSpec NewSpec(int no, SeedPath path, int i)
    {
        var s = new CaseSpec { No = no, Path = path };
        s.Owner = w.U(no % 2 == 0 ? "intake.bravo" : "intake.alpha");
        var sod = path == SeedPath.PendingApproval && i < 2;
        s.Reviewer = w.U(sod || no % 5 == 0 ? "dual.juliet" : "reviewer.charlie");
        s.Approver = s.Reviewer.UserName == "dual.juliet" ? w.U("manager.echo") : w.U(no % 3 == 0 ? "dual.juliet" : "manager.echo");
        s.Scheduler = w.U("scheduler.delta");
        s.Canceller = w.U("manager.echo");
        s.Attendant = w.U(no % 2 == 0 ? "attendant.golf" : "attendant.foxtrot");

        s.Registry = path switch
        {
            SeedPath.Draft => RegistryPlan.None,
            SeedPath.Submitted => i < 7 ? new[] { RegistryPlan.Valid, RegistryPlan.Flagged, RegistryPlan.Unknown, RegistryPlan.NotFound, RegistryPlan.Pending, RegistryPlan.Valid, RegistryPlan.Pending }[i] : RegistryPlan.None,
            SeedPath.CancelledSubmitted => i % 2 == 0 ? RegistryPlan.Valid : RegistryPlan.None,
            SeedPath.NeedsInformation => i % 4 == 3 ? RegistryPlan.Unknown : RegistryPlan.Valid,
            SeedPath.Eligible when i is 5 or 6 => RegistryPlan.Unknown,
            SeedPath.Approved when i == 4 => RegistryPlan.Unknown,
            SeedPath.Closed when i == 2 => RegistryPlan.Unknown,
            SeedPath.RejectedEligibility => i < 3 ? RegistryPlan.Flagged : i == 3 ? RegistryPlan.NotFound : RegistryPlan.Valid,
            _ => RegistryPlan.Valid,
        };
        s.Override = s.Registry == RegistryPlan.Unknown && SeedMix.PassesEligibility(path);
        if (path == SeedPath.Submitted && i < 7)
        {
            s.RegistryAttemptId = SeedIds.Guid($"seed-att-registry-{i}");
            s.RegistryDetail = i == 4 ? "Verification pending: the registry is rate limiting (retry after 30 s)."
                : i == 6 ? "Verification pending: the registry did not answer in time." : null;
        }
        PickApplicant(s);

        s.Category = PickCategory();
        s.Title = Title(s.Category, s.ApplicantName, s.Organization, no);
        s.Duration = PickDuration(s.Category);
        s.Attendees = s.Category.Code switch { "FLYOVER" => 80 + _rnd.Next(0, 160), "CONCERT" => 40 + _rnd.Next(0, 90), "COMMEMORATION" => 30 + _rnd.Next(0, 120), _ => 8 + _rnd.Next(0, 60) };
        if (path == SeedPath.Draft) s.WizardSections = 1 + i % 4;

        s.AnsweredRequests = path switch
        {
            SeedPath.Submitted when i is >= 7 and <= 11 => 1,
            SeedPath.Eligible when i < 3 => 1,
            SeedPath.RejectedEligibility when i is 4 or 5 => 1,
            SeedPath.TentativelyReserved when i < 2 => 1,
            SeedPath.PendingApproval when i == 0 => 1,
            SeedPath.Closed when i < 2 => 1,
            SeedPath.Permitted when i == 2 => 1,
            _ => 0,
        };
        if (path == SeedPath.NeedsInformation) s.OpenRequest = i < 5 ? RequestPlan.Overdue : RequestPlan.Open;

        s.Reason = path switch
        {
            SeedPath.RejectedEligibility => s.Registry switch
            {
                RegistryPlan.Flagged => "The applicant registry flags this applicant; eligibility cannot be confirmed under the current rules.",
                RegistryPlan.NotFound => "No applicant registry record exists for the reference supplied, and no alternative evidence was provided.",
                _ => Pick(i, "Proof of affiliation was illegible and the replacement did not match the applicant.",
                    "The event plan describes amplified music that the site rules do not allow.",
                    "The requested programme conflicts with the site's quiet-hours policy.",
                    "The insurance certificate expires before the requested event date.",
                    "The event plan does not name marshals for the expected attendance.",
                    "The applicant asked for a venue that does not accept this kind of event."),
            },
            SeedPath.RejectedApproval => Pick(i, "Attendance estimate exceeds what marshals can safely manage on that date.",
                "Final review found the parking plan inadequate for the expected attendance.",
                "The programme overlaps a regional ceremony at the same site; the organizer was asked to reapply."),
            SeedPath.CancelledSubmitted or SeedPath.CancelledEligible or SeedPath.CancelledReserved or SeedPath.CancelledPending
                or SeedPath.CancelledApproved or SeedPath.CancelledPermitted => Pick(no, "Applicant withdrew the request in writing.",
                    "The family postponed the service until next year.", "Organizer cancelled because the honoree's family cannot travel.",
                    "Duplicate request; the organizer continues with another application.", "Organizer cancelled after the date was moved by the family."),
            _ => null,
        };

        switch (path)
        {
            case SeedPath.TentativelyReserved:
                s.Calendar = i is 6 or 7 ? CalendarPushState.Pending : i == 8 ? CalendarPushState.Failed : CalendarPushState.Pushed;
                break;
            case SeedPath.PendingApproval:
                s.Calendar = i == 5 ? CalendarPushState.Conflict : i == 6 ? CalendarPushState.Failed : CalendarPushState.Pushed;
                break;
            case SeedPath.Approved:
                s.AssignAttendant = i < 3;
                break;
            case SeedPath.Permitted:
                s.AssignAttendant = true;
                s.Reissue = i == 1;
                s.Revoke = i == 2;
                s.ReadinessDone = i == 0 ? 2 : i == 1 ? 1 : 0;
                break;
            case SeedPath.CancelledPermitted:
                s.AssignAttendant = true;
                break;
            case SeedPath.CheckedIn:
                s.AssignAttendant = true;
                s.ReadinessDone = i == 0 ? 4 : 5;
                if (i == 0) s.Incidents.Add(new(IncidentSeverity.High, IncidentStatus.Open, "Crowd management", "Attendance exceeded the marshalled area near the north path; extra barriers requested."));
                if (i == 1) s.Incidents.Add(new(IncidentSeverity.Medium, IncidentStatus.Open, "Accessibility", "Accessible shuttle arrived late; two guests waited at the gate."));
                break;
            case SeedPath.EventCompleted:
                s.AssignAttendant = true;
                s.ReadinessDone = 5;
                if (i == 0) s.Incidents.Add(new(IncidentSeverity.Critical, IncidentStatus.Escalated, "Medical", "A guest collapsed during the address; first aid given and emergency services called (fictional).", Photo: true));
                if (i == 1) s.Incidents.Add(new(IncidentSeverity.Low, IncidentStatus.Open, "Lost property", "A folding chair set was left behind at the venue."));
                if (i == 2) s.Incidents.Add(new(IncidentSeverity.Medium, IncidentStatus.Open, "Property damage", "Tent peg damaged turf beside a headstone; repair needed.", Overdue: true));
                break;
            case SeedPath.Closed:
                s.AssignAttendant = true;
                s.ReadinessDone = 5;
                var closedIncidents = new IncidentPlan?[]
                {
                    new(IncidentSeverity.Critical, IncidentStatus.Resolved, "Vehicle access", "A service vehicle blocked the emergency lane during the ceremony.", Photo: true),
                    new(IncidentSeverity.High, IncidentStatus.Resolved, "Crowd management", "Overflow crowd stood on graves at the west edge until marshals moved them."),
                    new(IncidentSeverity.High, IncidentStatus.Resolved, "Weather", "Sudden gusts lifted a shade tent; event paused for ten minutes."),
                    new(IncidentSeverity.Medium, IncidentStatus.Resolved, "Noise complaint", "Neighbouring service asked for the speakers to be turned down."),
                    new(IncidentSeverity.Medium, IncidentStatus.Resolved, "Accessibility", "Ramp to the chapel was partly blocked by equipment cases."),
                    new(IncidentSeverity.Low, IncidentStatus.Resolved, "Lost property", "A guest's umbrella was handed in after the event."),
                    new(IncidentSeverity.Low, IncidentStatus.Resolved, "Property damage", "A flower stand was knocked over; no lasting damage."),
                    null,
                }[i];
                if (closedIncidents is not null) s.Incidents.Add(closedIncidents);
                break;
        }
        return s;
    }

    private void PickApplicant(CaseSpec s)
    {
        ReferenceData.ApplicantDef? def = s.Registry switch
        {
            RegistryPlan.Unknown => Next(RegistryEligibility.Unknown, ref _unknown),
            RegistryPlan.Flagged => Next(RegistryEligibility.Flagged, ref _flagged),
            RegistryPlan.NotFound => null,
            _ => Next(RegistryEligibility.Valid, ref _valid),
        };
        if (def is null)
        {
            var n = _missing++;
            string[] first = ["Avery", "Blair", "Casey", "Devon", "Emery", "Finley"];
            string[] last = ["Lindqvist", "Okafor", "Marchetti", "Haldane", "Pryce", "Castellan"];
            s.RegistryReference = $"REF-{10101 + n}";
            s.ApplicantName = $"{first[n % first.Length]} Practice-{last[n % last.Length]}";
            s.Organization = "";
            s.Email = $"unregistered{n + 1:000}@example.test";
            s.Phone = $"+1-555-02{n:00}";
            return;
        }
        s.RegistryReference = def.ReferenceId;
        s.ApplicantName = def.MatchedName;
        s.Organization = def.Organization;
        s.Email = def.Email;
        s.Phone = def.Phone;
    }

    private ReferenceData.ApplicantDef Next(RegistryEligibility e, ref int counter)
    {
        var pool = w.Registry.Applicants.Where(a => a.EligibilityStatus == e).OrderBy(a => a.ReferenceId, StringComparer.Ordinal).ToList();
        return pool[counter++ % pool.Count];
    }

    private EventCategory PickCategory()
    {
        var r = _rnd.Next(100);
        var code = r < 40 ? "MEMORIAL" : r < 60 ? "WREATH" : r < 80 ? "COMMEMORATION" : r < 90 ? "CONCERT" : "FLYOVER";
        return w.Categories.FirstOrDefault(c => c.Code == code) ?? w.Categories[0];
    }

    private int PickDuration(EventCategory c)
    {
        int[] options = c.Code switch
        {
            "CONCERT" => [120, 150, 180],
            "FLYOVER" => [90, 120],
            "WREATH" => [45, 60],
            _ => [45, 60, 60, 90, 90, 120],
        };
        return options[_rnd.Next(options.Length)];
    }

    public static string Title(EventCategory c, string name, string org, int variant)
    {
        var surname = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Practice";
        var group = string.IsNullOrWhiteSpace(org) ? $"the {surname} family" : org;
        return (c.Code, variant % 2) switch
        {
            ("MEMORIAL", 0) => $"Memorial service for the {surname} family",
            ("MEMORIAL", _) => $"Graveside remembrance hosted by {group}",
            ("COMMEMORATION", 0) => $"Anniversary commemoration by {group}",
            ("COMMEMORATION", _) => $"Remembrance ceremony of {group}",
            ("WREATH", 0) => $"Wreath laying by {group}",
            ("WREATH", _) => $"Dawn wreath laying ({group})",
            ("FLYOVER", _) => $"Ceremonial flyover and remembrance by {group}",
            ("CONCERT", 0) => $"Remembrance concert by {group}",
            _ => $"Evening of remembrance music ({group})",
        };
    }

    private static string Pick(int i, params string[] options) => options[i % options.Length];

    // ------------------------------------------------------------------ placement

    private void PlaceEventDay()
    {
        var anchor = w.Anchor;
        var grace = w.TokenGraceHours;
        var permitted = Of(SeedPath.Permitted);
        Place(permitted[0], 0, 1, true, t => t >= anchor.AddMinutes(30));
        Place(permitted[1], 0, 1, true, t => t >= anchor.AddHours(2));
        Place(permitted[2], 2, 1, true);
        Place(permitted[3], -1, -1, true, t => t.AddMinutes(permitted[3].Duration!.Value).AddHours(grace) < anchor.AddHours(-1));
        foreach (var c in Of(SeedPath.CheckedIn)) Place(c, 0, -1, true, t => t <= anchor.AddMinutes(-25));
        var completed = Of(SeedPath.EventCompleted);
        for (var i = 0; i < completed.Count; i++)
        {
            var c = completed[i];
            Place(c, -i, -1, true, t => t.AddMinutes(c.Duration!.Value + 40) <= anchor);
        }
        var closed = Of(SeedPath.Closed);
        for (var i = 0; i < closed.Count; i++)
        {
            var c = closed[i];
            Place(c, -(3 + i * 5), -1, true, t => t.AddMinutes(c.Duration!.Value).AddDays(1).AddHours(3) <= anchor);
        }
    }

    private void PlaceWeatherCases()
    {
        var approved = Of(SeedPath.Approved);
        var severe = w.Weather.Venues.SelectMany(wv => wv.SevereDays.Where(d => d >= 1).Select(d => (wv.VenueCode, Day: d)))
            .OrderBy(x => x.Day).ThenBy(x => x.VenueCode, StringComparer.Ordinal).ToList();
        var targets = new[] { (Spec: approved[0], Plan: WeatherPlan.SevereUnacknowledged), (Spec: approved[1], Plan: WeatherPlan.SevereAcknowledged) };
        var used = 0;
        foreach (var (spec, weatherPlan) in targets)
        {
            var placed = false;
            while (!placed && used < severe.Count)
            {
                var (code, day) = severe[used++];
                var venue = w.Venues.FirstOrDefault(v => v.Code == code);
                if (venue is null) continue;
                if (!Allows(venue, spec.Category)) spec.Category = w.Category("MEMORIAL");
                spec.Title = Title(spec.Category, spec.ApplicantName, spec.Organization, spec.No);
                var date = w.LocalToday(venue).AddDays(day);
                var start = Slots(venue, date, spec.Duration!.Value, 600, true).Cast<DateTimeOffset?>().FirstOrDefault();
                if (start is null) continue;
                Commit(spec, venue, start.Value, true);
                spec.Weather = weatherPlan;
                placed = true;
            }
            if (!placed) Place(spec, 4, 1, true);
        }
    }

    private void PlaceCalendarConflict()
    {
        var spec = Of(SeedPath.PendingApproval)[5];
        spec.Category = w.Category("MEMORIAL");
        spec.Title = Title(spec.Category, spec.ApplicantName, spec.Organization, spec.No);
        foreach (var ext in _plan.External.Where(e => e.Start > w.Anchor.AddDays(4) && e.Start < w.Anchor.AddDays(18)).OrderBy(e => e.Start))
        {
            var venue = ext.Venue;
            var date = SeedWorld.LocalDate(venue, ext.Start);
            var facts = SeedTimeline.Facts(venue);
            var duration = spec.Duration!.Value;
            foreach (var start in Slots(venue, date, duration, 600, false))
            {
                var (bs, be) = ConflictEngine.Block(facts, start, duration);
                if (!ConflictEngine.Overlaps(bs, be, ext.Start, ext.End)) continue;
                if (!_plan.Timeline.IsFree(venue.Id, bs, be, (ext.Start, ext.End))) continue;
                spec.Alternatives.AddRange(Slots(venue, date, duration, 600, true).Take(2));
                if (spec.Alternatives.Count == 0) continue;
                Commit(spec, venue, start, true);
                spec.CalendarConflict = ext;
                return;
            }
        }
        spec.Calendar = CalendarPushState.Pushed; // no external event fits; the case stays an ordinary pending approval
        Place(spec, 9, 1, true);
    }

    private void PlaceRemainingActive()
    {
        var order = new (SeedPath Path, int Day, int Step)[]
        {
            (SeedPath.Approved, 4, 2), (SeedPath.PendingApproval, 5, 2), (SeedPath.TentativelyReserved, 7, 2),
        };
        foreach (var (path, day, step) in order)
        {
            var list = Of(path);
            for (var i = 0; i < list.Count; i++)
                if (list[i].Venue is null) Place(list[i], day + i * step, 1, true);
        }
    }

    private void PlaceReleased()
    {
        var active = _plan.Cases.Where(c => SeedMix.HasReservation(c.Path) && !SeedMix.ReservationReleased(c.Path) && c.Start > w.Anchor.AddDays(5)).OrderBy(c => c.No).ToList();
        var rejected = Of(SeedPath.RejectedApproval);
        // Test collisions: two released holds sat on the same slot as a reservation that is active now.
        OverlapActive(rejected[0], active[0]);
        OverlapActive(Of(SeedPath.CancelledPending)[0], active[3 % active.Count]);
        for (var i = 1; i < rejected.Count; i++) Place(rejected[i], 6 + i * 4, 1, true, active: false);
        Place(Of(SeedPath.CancelledReserved)[0], 8, 1, true, active: false);
        Place(Of(SeedPath.CancelledApproved)[0], 10, 1, true, active: false);
        Place(Of(SeedPath.CancelledPermitted)[0], 5, 1, true, active: false);
    }

    private void PlaceProposed()
    {
        var eligible = Of(SeedPath.Eligible);
        var holds = _plan.Cases.Where(c => SeedMix.HasReservation(c.Path) && !SeedMix.ReservationReleased(c.Path) && c.Start > w.Anchor.AddDays(6)).OrderBy(c => c.No).ToList();
        OverlapActive(eligible[0], holds[1 % holds.Count], "an existing reservation");
        OverlapActive(eligible[1], holds[2 % holds.Count], "an existing reservation");
        var blackout = _plan.Blackouts.First(b => b.Start > w.Anchor.AddDays(5));
        PlaceOverlapping(eligible[2], blackout.Venue, blackout.Start, blackout.End, $"a venue closure ({blackout.Reason})");
        var ext = _plan.External.First(e => e.Start > w.Anchor.AddDays(6));
        PlaceOverlapping(eligible[3], ext.Venue, ext.Start, ext.End, $"regional calendar event {ext.ExternalId}");
        var partnerVenue = w.Venues.First(v => v.Code == "CHG-LAWN");
        var ps = SeedWorld.Local(partnerVenue, w.LocalToday(partnerVenue).AddDays(5), new TimeOnly(11, 0));
        PlaceOverlapping(eligible[4], partnerVenue, ps, ps.AddMinutes(90), "partner event PRT-S002");
        var submitted = Of(SeedPath.Submitted);
        var b2 = _plan.Blackouts.Where(b => b.Start > w.Anchor.AddDays(12)).Skip(1).First();
        PlaceOverlapping(submitted[0], b2.Venue, b2.Start, b2.End, $"a venue closure ({b2.Reason})");

        foreach (var (path, day, step) in new (SeedPath, int, int)[]
                 {
                     (SeedPath.Eligible, 7, 3), (SeedPath.Submitted, 10, 3), (SeedPath.NeedsInformation, 12, 2), (SeedPath.CancelledSubmitted, 9, 4),
                     (SeedPath.CancelledEligible, 11, 4), (SeedPath.Draft, 14, 3),
                 })
        {
            var list = Of(path);
            for (var i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c.Venue is not null) continue;
                if (path == SeedPath.Draft && c.WizardSections < 2) { c.Duration = null; c.Attendees = null; continue; }
                if (!Place(c, day + i * step, 1, true, active: false)) Place(c, day + i * step, 1, false, active: false);
            }
        }
        var rejectedElig = Of(SeedPath.RejectedEligibility);
        for (var i = 0; i < rejectedElig.Count; i++)
        {
            var c = rejectedElig[i];
            if (i < 5) Place(c, 10 + i * 3, 1, true, active: false);
            else Place(c, -(5 + i * 4), -1, true, null, false);
        }
    }

    private bool Place(CaseSpec s, int dayOffset, int direction, bool requireFree, Func<DateTimeOffset, bool>? accept = null, bool active = true)
    {
        var duration = s.Duration ??= PickDuration(s.Category);
        var venues = w.Venues.Where(v => Allows(v, s.Category)).OrderBy(_ => _rnd.Next()).ToList();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var offset = dayOffset + attempt * direction;
            foreach (var venue in venues)
            {
                var date = w.LocalToday(venue).AddDays(offset);
                if (_plan.Timeline.IsSevere(venue.Id, date)) continue;
                var preferred = 540 + _rnd.Next(0, 12) * 30;
                foreach (var start in Slots(venue, date, duration, preferred, requireFree))
                {
                    if (accept is not null && !accept(start)) continue;
                    Commit(s, venue, start, active);
                    return true;
                }
            }
        }
        return false;
    }

    private void OverlapActive(CaseSpec s, CaseSpec hold, string? note = null)
    {
        s.Category = hold.Category;
        s.Title = Title(s.Category, s.ApplicantName, s.Organization, s.No);
        PlaceOverlapping(s, hold.Venue!, hold.Start!.Value, hold.End!.Value, note ?? $"released hold overlapping {hold.Key}");
    }

    /// <summary>A proposed or released slot that deliberately overlaps <paramref name="windowStart"/>..<paramref name="windowEnd"/> (never stored as an active hold).</summary>
    private void PlaceOverlapping(CaseSpec s, Venue venue, DateTimeOffset windowStart, DateTimeOffset windowEnd, string note)
    {
        if (!Allows(venue, s.Category))
        {
            s.Category = w.Category("MEMORIAL");
            s.Title = Title(s.Category, s.ApplicantName, s.Organization, s.No);
        }
        var duration = s.Duration!.Value;
        var facts = SeedTimeline.Facts(venue);
        var date = SeedWorld.LocalDate(venue, windowStart);
        var start = Slots(venue, date, duration, (int)TimeZoneInfo.ConvertTime(windowStart, SeedWorld.Zone(venue)).TimeOfDay.TotalMinutes, false)
            .Where(t =>
            {
                var (bs, be) = ConflictEngine.Block(facts, t, duration);
                return ConflictEngine.Overlaps(bs, be, windowStart, windowEnd);
            })
            .Cast<DateTimeOffset?>().FirstOrDefault();
        if (start is null)
        {
            Place(s, 15, 1, true, active: false);
            return;
        }
        Commit(s, venue, start.Value, false);
        s.CollisionNote = note;
    }

    private void Commit(CaseSpec s, Venue venue, DateTimeOffset start, bool active)
    {
        var duration = s.Duration!.Value;
        s.Venue = venue;
        s.Start = start;
        s.Attendees = Math.Clamp(s.Attendees ?? 20, 1, venue.Capacity);
        var (bs, be) = ConflictEngine.Block(SeedTimeline.Facts(venue), start, duration);
        if (active) _plan.Timeline.Block(venue.Id, bs, be);
        if (!w.Resources.TryGetValue(venue.CemeteryId, out var stock)) return;
        var wanted = new List<(string Code, int Quantity)> { ("CHAIRS", Math.Min(s.Attendees.Value, 120)) };
        var extras = new (string Code, int Quantity)[] { ("TENTS", 1 + _rnd.Next(2)), ("PARKING", 1 + _rnd.Next(2)), ("AV", 1), ("PODIUM", 1), ("FLAGS", 1 + _rnd.Next(2)), ("SHUTTLE", 1), ("BARRIERS", 4 + _rnd.Next(7)) };
        var count = _rnd.Next(1, 4);
        for (var k = 0; k < count; k++) wanted.Add(extras[_rnd.Next(extras.Length)]);
        foreach (var (code, quantity) in wanted.GroupBy(x => x.Code).Select(g => (g.Key, g.Max(x => x.Quantity))))
        {
            var resource = stock.FirstOrDefault(r => r.Code == code);
            if (resource is null) continue;
            var q = active ? Math.Min(quantity, _plan.Timeline.Available(resource, bs, be)) : quantity;
            if (q <= 0) continue;
            s.Resources.Add((resource, q));
            if (active) _plan.Timeline.Allocate(resource, bs, be, q);
        }
    }

    /// <summary>Candidate starts on one venue-local day in 30-minute steps, nearest to <paramref name="preferredMinute"/> first.</summary>
    private IEnumerable<DateTimeOffset> Slots(Venue venue, DateOnly date, int duration, int preferredMinute, bool requireFree)
    {
        var hours = venue.OpeningHours.FirstOrDefault(h => h.Day == date.DayOfWeek);
        if (hours is null) yield break;
        var facts = SeedTimeline.Facts(venue);
        var first = (int)hours.Opens.ToTimeSpan().TotalMinutes + venue.SetupBufferMinutes;
        first = (first + 29) / 30 * 30;
        var last = (int)hours.Closes.ToTimeSpan().TotalMinutes - duration - venue.TeardownBufferMinutes;
        var minutes = new List<int>();
        for (var m = first; m <= last; m += 30) minutes.Add(m);
        foreach (var m in minutes.OrderBy(m => Math.Abs(m - preferredMinute)).ThenBy(m => m))
        {
            var start = SeedWorld.Local(venue, date, new TimeOnly(m / 60, m % 60));
            if (!ConflictEngine.WithinOpeningHours(facts, start, start.AddMinutes(duration))) continue;
            var (bs, be) = ConflictEngine.Block(facts, start, duration);
            if (requireFree && !_plan.Timeline.IsFree(venue.Id, bs, be)) continue;
            yield return start;
        }
    }

    private static bool Allows(Venue v, EventCategory c) => v.AllowedCategoryCodes.Count == 0 || v.AllowedCategoryCodes.Contains(c.Code);

    // ------------------------------------------------------------------ timing

    /// <summary>
    /// Creation time and the latest moment the pre-event workflow may reach: an hour before the anchor, 14 hours before a
    /// near event, two days before a past one, and well in the past for overdue information requests.
    /// </summary>
    private void Time(CaseSpec s)
    {
        var anchor = w.Anchor;
        if (s.Path == SeedPath.Draft)
        {
            s.Created = Trim(anchor.AddHours(-(2 + _rnd.Next(0, 140))));
            s.Limit = anchor;
            return;
        }
        var limit = anchor.AddHours(-1);
        if (s.Start is { } start)
        {
            var cap = start < anchor ? start.AddDays(-2) : start.AddHours(-14);
            if (cap < limit) limit = cap;
        }
        if (s.OpenRequest == RequestPlan.Overdue) limit = Min(limit, anchor.AddDays(-9 - _rnd.Next(0, 4)));
        var days = s.Path switch
        {
            SeedPath.Submitted or SeedPath.CancelledSubmitted => 2 + _rnd.Next(0, 6),
            SeedPath.NeedsInformation => s.OpenRequest == RequestPlan.Open ? 2 + _rnd.Next(0, 3) : 6 + _rnd.Next(0, 4),
            _ => 8 + _rnd.Next(0, 14),
        };
        if (s.AnsweredRequests > 0) days += 4;
        s.Limit = Trim(limit);
        s.Created = Trim(limit.AddDays(-days).AddHours(-_rnd.Next(0, 8)));
    }

    // ------------------------------------------------------------------ notifications (25)

    private void PickNotifications()
    {
        var picks = new List<(CaseSpec Spec, string Template)>();
        var permitted = Of(SeedPath.Permitted);
        picks.Add((permitted[0], "permit.issued"));
        picks.Add((permitted[1], "permit.reissued"));
        picks.Add((permitted[2], "permit.revoked"));
        picks.Add((permitted[3], "permit.issued"));
        picks.AddRange(Of(SeedPath.Approved).Select(c => (c, "approval.approved")));
        picks.AddRange(Of(SeedPath.NeedsInformation).Where(c => c.OpenRequest == RequestPlan.Open).Take(5).Select(c => (c, "info.requested")));
        picks.AddRange(Of(SeedPath.Submitted).TakeLast(4).Select(c => (c, "application.submitted")));
        picks.AddRange(Of(SeedPath.RejectedEligibility).Take(2).Select(c => (c, "eligibility.rejected")));
        picks.Add((Of(SeedPath.CancelledPermitted)[0], "case.cancelled"));
        picks.Add((Of(SeedPath.CancelledApproved)[0], "case.cancelled"));
        picks.AddRange(Of(SeedPath.TentativelyReserved).Take(2).Select(c => (c, "reservation.tentative")));

        // Delivered ones are exactly those with an accepted delivery webhook (seven); the rest await the gateway or failed.
        var states = Enumerable.Repeat(NotificationStatus.Delivered, 7).Concat(Enumerable.Repeat(NotificationStatus.Sent, 9))
            .Concat(Enumerable.Repeat(NotificationStatus.Failed, 5)).Concat(Enumerable.Repeat(NotificationStatus.Queued, 4)).ToArray();
        _rnd.Shuffle(states);
        for (var i = 0; i < picks.Count && i < states.Length; i++) picks[i].Spec.Notify[picks[i].Template] = states[i];
    }

    // ------------------------------------------------------------------ helpers

    public static DateTimeOffset Trim(DateTimeOffset t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, t.Offset);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>Deterministic GUIDs for seed rows that other seed rows point at (stable across re-runs).</summary>
public static class SeedIds
{
    public static Guid Guid(string key) => new(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
}
