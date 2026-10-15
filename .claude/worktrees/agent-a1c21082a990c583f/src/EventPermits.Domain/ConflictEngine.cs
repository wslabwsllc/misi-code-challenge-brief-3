namespace EventPermits.Domain;

public enum ConflictKind
{
    VenueInactive,
    CategoryNotAllowed,
    OutsideOpeningHours,
    Blackout,
    ReservationOverlap,
    ExternalCalendarEvent,
    PartnerEvent,
    OverCapacity,
    InvalidAttendees,
    InvalidDuration,
    ResourceShortage,
    InPast,
}

public sealed record Conflict(ConflictKind Kind, string Message, Guid? RecordId = null, DateTimeOffset? Start = null, DateTimeOffset? End = null);

/// <summary>An occupied window that a new reservation must not overlap (already buffered where relevant).</summary>
public sealed record BusyWindow(ConflictKind Kind, Guid Id, string Label, DateTimeOffset Start, DateTimeOffset End);

public sealed record ResourceDemand(Guid ResourceId, string Name, int Requested, int Total, int AllocatedElsewhere);

public sealed record SlotRequest(
    DateTimeOffset StartUtc,
    int DurationMinutes,
    int AttendeeCount,
    string? CategoryCode,
    IReadOnlyList<ResourceDemand> Resources);

public sealed record VenueFacts(
    Guid Id,
    bool IsActive,
    int Capacity,
    int SetupBufferMinutes,
    int TeardownBufferMinutes,
    IReadOnlyList<OpeningHours> OpeningHours,
    IReadOnlyList<string> AllowedCategoryCodes,
    TimeZoneInfo TimeZone);

/// <summary>
/// Pure reservation check (FR-06). Returns every violation, not just the first.
/// Intervals are half-open [start, end): touching windows do not collide.
/// </summary>
public static class ConflictEngine
{
    public static (DateTimeOffset BlockStart, DateTimeOffset BlockEnd) Block(VenueFacts venue, DateTimeOffset start, int durationMinutes) =>
        (start.AddMinutes(-venue.SetupBufferMinutes), start.AddMinutes(durationMinutes + venue.TeardownBufferMinutes));

    public static IReadOnlyList<Conflict> Check(
        VenueFacts venue,
        SlotRequest slot,
        IEnumerable<BusyWindow> busy,
        DateTimeOffset now,
        int minDuration = 30,
        int maxDuration = 300)
    {
        var conflicts = new List<Conflict>();
        if (!venue.IsActive) conflicts.Add(new(ConflictKind.VenueInactive, "The venue is not active."));
        if (slot.CategoryCode is { } code && venue.AllowedCategoryCodes.Count > 0 && !venue.AllowedCategoryCodes.Contains(code))
            conflicts.Add(new(ConflictKind.CategoryNotAllowed, $"The venue does not accept events of category {code}."));
        if (slot.DurationMinutes < minDuration || slot.DurationMinutes > maxDuration)
            conflicts.Add(new(ConflictKind.InvalidDuration, $"Duration must be between {minDuration} and {maxDuration} minutes."));
        if (slot.AttendeeCount < 1)
            conflicts.Add(new(ConflictKind.InvalidAttendees, "At least one attendee is required."));
        else if (slot.AttendeeCount > venue.Capacity)
            conflicts.Add(new(ConflictKind.OverCapacity, $"{slot.AttendeeCount} attendees exceed the venue capacity of {venue.Capacity}."));
        if (slot.StartUtc <= now)
            conflicts.Add(new(ConflictKind.InPast, "The event start must be in the future."));

        var end = slot.StartUtc.AddMinutes(Math.Max(slot.DurationMinutes, 0));
        if (!WithinOpeningHours(venue, slot.StartUtc, end))
            conflicts.Add(new(ConflictKind.OutsideOpeningHours, "The event (including setup and teardown) is outside the venue's opening hours."));

        var (blockStart, blockEnd) = Block(venue, slot.StartUtc, Math.Max(slot.DurationMinutes, 0));
        foreach (var window in busy)
        {
            // Blackouts and external/partner events block the event itself plus its buffers;
            // other reservations are already stored with their own buffers.
            if (Overlaps(blockStart, blockEnd, window.Start, window.End))
                conflicts.Add(new(window.Kind, Describe(window), window.Id, window.Start, window.End));
        }

        foreach (var demand in slot.Resources)
        {
            var available = demand.Total - demand.AllocatedElsewhere;
            if (demand.Requested < 0)
                conflicts.Add(new(ConflictKind.ResourceShortage, $"{demand.Name}: quantity cannot be negative.", demand.ResourceId));
            else if (demand.Requested > available)
                conflicts.Add(new(ConflictKind.ResourceShortage,
                    $"{demand.Name}: {demand.Requested} requested but only {Math.Max(available, 0)} of {demand.Total} available for this time.", demand.ResourceId));
        }
        return conflicts;
    }

    public static bool Overlaps(DateTimeOffset aStart, DateTimeOffset aEnd, DateTimeOffset bStart, DateTimeOffset bEnd) =>
        aStart < bEnd && bStart < aEnd;

    /// <summary>The buffered block must fit inside one day's opening hours in the venue's own time zone.</summary>
    public static bool WithinOpeningHours(VenueFacts venue, DateTimeOffset start, DateTimeOffset end)
    {
        if (venue.OpeningHours.Count == 0) return true;
        var blockStart = TimeZoneInfo.ConvertTime(start.AddMinutes(-venue.SetupBufferMinutes), venue.TimeZone);
        var blockEnd = TimeZoneInfo.ConvertTime(end.AddMinutes(venue.TeardownBufferMinutes), venue.TimeZone);
        if (blockStart.Date != blockEnd.Date && blockEnd.TimeOfDay != TimeSpan.Zero) return false;
        var hours = venue.OpeningHours.FirstOrDefault(h => h.Day == blockStart.DayOfWeek);
        if (hours is null) return false;
        var opens = blockStart.Date + hours.Opens.ToTimeSpan();
        var closes = blockStart.Date + hours.Closes.ToTimeSpan();
        return blockStart.DateTime >= opens && blockEnd.DateTime <= closes;
    }

    /// <summary>Up to <paramref name="count"/> free starts on the same local day, in 30-minute steps, nearest first.</summary>
    public static IReadOnlyList<DateTimeOffset> SuggestFreeSlots(
        VenueFacts venue, SlotRequest slot, IReadOnlyList<BusyWindow> busy, DateTimeOffset now, int count = 3, int minDuration = 30, int maxDuration = 300)
    {
        var local = TimeZoneInfo.ConvertTime(slot.StartUtc, venue.TimeZone);
        var dayStart = new DateTimeOffset(local.Date, local.Offset);
        var candidates = Enumerable.Range(0, 48)
            .Select(i => dayStart.AddMinutes(i * 30))
            .OrderBy(c => Math.Abs((c - slot.StartUtc).TotalMinutes));
        var result = new List<DateTimeOffset>();
        foreach (var candidate in candidates)
        {
            var probe = slot with { StartUtc = candidate.ToUniversalTime(), Resources = [] };
            var problems = Check(venue, probe, busy, now, minDuration, maxDuration)
                .Where(c => c.Kind is not (ConflictKind.OverCapacity or ConflictKind.InvalidAttendees or ConflictKind.CategoryNotAllowed or ConflictKind.InvalidDuration));
            if (!problems.Any()) result.Add(candidate.ToUniversalTime());
            if (result.Count == count) break;
        }
        return result.OrderBy(r => r).ToList();
    }

    private static string Describe(BusyWindow window) => window.Kind switch
    {
        ConflictKind.Blackout => $"Venue closed: {window.Label}.",
        ConflictKind.ReservationOverlap => $"Overlaps reservation {window.Label} (setup and teardown buffers included).",
        ConflictKind.ExternalCalendarEvent => $"Overlaps regional calendar event {window.Label}.",
        ConflictKind.PartnerEvent => $"Overlaps partner event {window.Label}.",
        _ => window.Label,
    };
}
