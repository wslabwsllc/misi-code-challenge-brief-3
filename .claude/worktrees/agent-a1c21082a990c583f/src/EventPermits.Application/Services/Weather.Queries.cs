using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public sealed record UpcomingWeatherRow(Guid CaseId, string Reference, CaseStatus Status, Guid VenueId, string VenueName, string TimeZoneId, DateTimeOffset StartUtc,
    DateOnly LocalDate, WeatherAdvisory? Advisory, DateTimeOffset? RetrievedAt, bool Fresh, bool Acknowledged);

public sealed record CachedForecastRow(Guid VenueId, string VenueCode, string VenueName, DateOnly Date, string Summary, WeatherAdvisory Advisory,
    DateTimeOffset SourceTimestampUtc, DateTimeOffset RetrievedAt, bool Fresh);

/// <summary>Page 24 overview reads: upcoming events with their cached advisory, and the forecast cache with freshness (E7-05).</summary>
public sealed partial class WeatherQueryService
{
    private static readonly CaseStatus[] Upcoming =
        [CaseStatus.TentativelyReserved, CaseStatus.PendingApproval, CaseStatus.Approved, CaseStatus.Permitted, CaseStatus.CheckedIn];

    /// <summary>Events in the caller's scope over the next <paramref name="days"/> days. Reads the cache only; never calls the weather service.</summary>
    public async Task<IReadOnlyList<UpcomingWeatherRow>> UpcomingAsync(Actor actor, int days = 14, CancellationToken ct = default)
    {
        if (!actor.Can(Cap.ViewWeather) && !actor.Can(Cap.ApproveCases)) throw new ForbiddenException();
        var cacheMinutes = await rules.IntAsync(RuleKeys.WeatherCacheMinutes, ct);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var from = Now.AddHours(-12);
        var to = Now.AddDays(Math.Clamp(days, 1, 31));
        var rows = await Scoped(db, actor).AsNoTracking().Where(a => Upcoming.Contains(a.Status))
            .SelectMany(a => a.Reservations.Where(r => r.Status != ReservationStatus.Released && r.StartUtc >= from && r.StartUtc < to),
                (a, r) => new { a.Id, a.Reference, a.Status, r.VenueId, Venue = r.Venue!.Name, r.Venue.Cemetery!.TimeZoneId, r.StartUtc, Ack = a.WeatherAcknowledgedAt != null })
            .OrderBy(x => x.StartUtc).Take(100).ToListAsync(ct);
        var venueIds = rows.Select(r => r.VenueId).Distinct().ToList();
        var snapshots = await db.WeatherSnapshots.AsNoTracking().Where(w => venueIds.Contains(w.VenueId) && w.Date >= DateOnly.FromDateTime(from.UtcDateTime).AddDays(-1))
            .ToListAsync(ct);
        return rows.Select(r =>
        {
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.StartUtc, CaseService.Zone(r.TimeZoneId)).DateTime);
            var snap = snapshots.Where(s => s.VenueId == r.VenueId && s.Date == localDate).MaxBy(s => s.RetrievedAt);
            return new UpcomingWeatherRow(r.Id, r.Reference, r.Status, r.VenueId, r.Venue, r.TimeZoneId, r.StartUtc, localDate, snap?.Advisory, snap?.RetrievedAt,
                snap is not null && !IsStale(snap.RetrievedAt, Now, cacheMinutes), r.Ack);
        }).ToList();
    }

    /// <summary>The newest cached forecast per venue and date, newest first, labelled Fresh or Stale against the cache rule.</summary>
    public async Task<IReadOnlyList<CachedForecastRow>> CacheAsync(Actor actor, CancellationToken ct = default)
    {
        if (!actor.Can(Cap.ViewWeather) && !actor.Can(Cap.ApproveCases)) throw new ForbiddenException();
        var cacheMinutes = await rules.IntAsync(RuleKeys.WeatherCacheMinutes, ct);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var recent = await db.WeatherSnapshots.AsNoTracking().OrderByDescending(w => w.RetrievedAt).Take(200)
            .Join(db.Venues, w => w.VenueId, v => v.Id, (w, v) => new { w, v.Name }).ToListAsync(ct);
        return recent.GroupBy(x => (x.w.VenueId, x.w.Date)).Select(g => g.First()).Take(25)
            .Select(x => new CachedForecastRow(x.w.VenueId, x.w.VenueCode, x.Name, x.w.Date, x.w.Summary, x.w.Advisory, x.w.SourceTimestampUtc, x.w.RetrievedAt,
                !IsStale(x.w.RetrievedAt, Now, cacheMinutes)))
            .ToList();
    }

    public async Task<int> CacheMinutesAsync(CancellationToken ct = default) => await rules.IntAsync(RuleKeys.WeatherCacheMinutes, ct);
}
