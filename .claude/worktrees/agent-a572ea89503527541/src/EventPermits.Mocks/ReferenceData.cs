using System.Text.Json;

namespace EventPermits.Mocks;

public sealed record Cemetery(string Code, string Name, string Address, string TimeZoneId);
public sealed record Venue(string Code, string CemeteryCode, string Name, string Address);
public sealed record Applicant(string ReferenceId, string MatchedName, string EligibilityStatus);
public sealed record CalendarSeed(string ExternalId, string VenueCode, string Title, int DayOffset, string LocalStart, int DurationMinutes);
public sealed record WeatherVenue(string VenueCode, double BaseTemperatureC, double BaseWindKph, List<int> SevereDays, List<int> AdvisoryDays);
public sealed record GeocodeCandidate(string Label, double Latitude, double Longitude, double Confidence);
public sealed record GeocodeEntry(string Query, string VenueCode, List<GeocodeCandidate> Candidates);

/// <summary>The shared fictional reference data in data/reference, loaded once at startup.</summary>
public sealed class ReferenceData
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record SitesFile(List<Cemetery> Cemeteries, List<Venue> Venues);
    private sealed record RegistryFile(List<Applicant> Applicants);
    private sealed record CalendarFile(List<CalendarSeed> Events);
    private sealed record WeatherFile(List<WeatherVenue> Venues);
    private sealed record GeocodeFile(List<GeocodeEntry> Entries);

    private readonly Dictionary<string, TimeZoneInfo> _venueZones;

    private ReferenceData(SitesFile sites, RegistryFile registry, CalendarFile calendar, WeatherFile weather, GeocodeFile geocode)
    {
        Venues = sites.Venues;
        VenuesByCode = sites.Venues.ToDictionary(v => v.Code, StringComparer.OrdinalIgnoreCase);
        var zones = sites.Cemeteries.ToDictionary(c => c.Code, c => TimeZoneInfo.FindSystemTimeZoneById(c.TimeZoneId), StringComparer.OrdinalIgnoreCase);
        _venueZones = sites.Venues.ToDictionary(v => v.Code, v => zones[v.CemeteryCode], StringComparer.OrdinalIgnoreCase);
        Applicants = registry.Applicants.ToDictionary(a => a.ReferenceId, StringComparer.OrdinalIgnoreCase);
        CalendarSeeds = calendar.Events;
        Weather = weather.Venues.ToDictionary(w => w.VenueCode, StringComparer.OrdinalIgnoreCase);
        Geocode = geocode.Entries.ToDictionary(e => e.Query.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Venue> Venues { get; }
    public IReadOnlyDictionary<string, Venue> VenuesByCode { get; }
    public IReadOnlyDictionary<string, Applicant> Applicants { get; }
    public IReadOnlyList<CalendarSeed> CalendarSeeds { get; }
    public IReadOnlyDictionary<string, WeatherVenue> Weather { get; }
    public IReadOnlyDictionary<string, GeocodeEntry> Geocode { get; }

    public static ReferenceData Load(string directory)
    {
        T Read<T>(string file)
        {
            var path = Path.Combine(directory, file);
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"Reference file {path} is empty.");
        }

        return new ReferenceData(
            Read<SitesFile>("sites.json"),
            Read<RegistryFile>("registry.json"),
            Read<CalendarFile>("calendar-events.json"),
            Read<WeatherFile>("weather.json"),
            Read<GeocodeFile>("geocode.json"));
    }

    /// <summary>The venue's IANA time zone, or UTC for an unknown venue.</summary>
    public TimeZoneInfo ZoneFor(string venueCode) =>
        _venueZones.TryGetValue(venueCode, out var zone) ? zone : TimeZoneInfo.Utc;

    public static DateOnly LocalDate(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));

    /// <summary>Converts a venue-local wall-clock time to UTC (a time skipped by DST moves forward one hour).</summary>
    public static DateTime LocalToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
