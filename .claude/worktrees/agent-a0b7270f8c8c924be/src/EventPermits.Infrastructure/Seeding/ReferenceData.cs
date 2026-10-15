using System.Text.Json;
using System.Text.Json.Serialization;
using EventPermits.Domain;

namespace EventPermits.Infrastructure.Seeding;

/// <summary>Committed fictional reference data shared with the mock services (data/reference/*.json, E12-02).</summary>
public static class ReferenceData
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

    public static string Directory(string? configured = null) =>
        string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppContext.BaseDirectory, "data", "reference") : configured;

    public static Sites LoadSites(string? dir = null) =>
        JsonSerializer.Deserialize<Sites>(File.ReadAllText(Path.Combine(Directory(dir), "sites.json")), Json)
        ?? throw new InvalidOperationException("sites.json is empty.");

    public static RegistryFile LoadRegistry(string? dir = null) =>
        JsonSerializer.Deserialize<RegistryFile>(File.ReadAllText(Path.Combine(Directory(dir), "registry.json")), Json)
        ?? throw new InvalidOperationException("registry.json is empty.");

    public static GeocodeFile LoadGeocode(string? dir = null) =>
        JsonSerializer.Deserialize<GeocodeFile>(File.ReadAllText(Path.Combine(Directory(dir), "geocode.json")), Json)
        ?? throw new InvalidOperationException("geocode.json is empty.");

    public static CalendarFile LoadCalendar(string? dir = null) =>
        JsonSerializer.Deserialize<CalendarFile>(File.ReadAllText(Path.Combine(Directory(dir), "calendar-events.json")), Json)
        ?? throw new InvalidOperationException("calendar-events.json is empty.");

    public static WeatherFile LoadWeather(string? dir = null) =>
        JsonSerializer.Deserialize<WeatherFile>(File.ReadAllText(Path.Combine(Directory(dir), "weather.json")), Json)
        ?? throw new InvalidOperationException("weather.json is empty.");

    public sealed record Sites(List<CemeteryDef> Cemeteries, List<VenueDef> Venues, List<ResourceDef> Resources);
    public sealed record CemeteryDef(string Code, string Name, string Address, string TimeZoneId);
    public sealed record VenueDef(string Code, string CemeteryCode, string Name, string Address, int Capacity, int SetupBufferMinutes,
        int TeardownBufferMinutes, List<HoursDef> OpeningHours, List<string> AllowedCategories);
    public sealed record HoursDef(DayOfWeek Day, string Opens, string Closes);
    public sealed record ResourceDef(string Code, string Name, string Unit, int QuantityPerCemetery);
    public sealed record RegistryFile(List<ApplicantDef> Applicants);
    public sealed record ApplicantDef(string ReferenceId, string MatchedName, RegistryEligibility EligibilityStatus, string Organization, string Email, string Phone);
    public sealed record GeocodeFile(List<GeocodeEntry> Entries);
    /// <summary>Regional calendar events; <c>DayOffset</c> is relative to the current UTC date, <c>LocalStart</c> is venue-local.</summary>
    public sealed record CalendarFile(List<CalendarEventDef> Events);
    public sealed record CalendarEventDef(string ExternalId, string VenueCode, string Title, int DayOffset, string LocalStart, int DurationMinutes);
    /// <summary>Weather baselines; severe and advisory days are offsets from the venue-local current date.</summary>
    public sealed record WeatherFile(List<WeatherVenueDef> Venues);
    public sealed record WeatherVenueDef(string VenueCode, double BaseTemperatureC, double BaseWindKph, List<int> SevereDays, List<int> AdvisoryDays);
    public sealed record GeocodeEntry(string Query, string VenueCode, List<GeocodeCandidateDef> Candidates);
    public sealed record GeocodeCandidateDef(string Label, double Latitude, double Longitude, double Confidence);
}

/// <summary>Demo accounts (OD-2). Every account uses the documented local password when Seed:AllowDemoPassword is true.</summary>
public static class DemoAccounts
{
    /// <summary>Documented in the README for local runs only; refused unless Seed:AllowDemoPassword=true (never in AWS).</summary>
    public const string DocumentedPassword = "EventPermits!Demo2026";

    public sealed record Account(string UserName, string DisplayName, string[] Roles);

    public static readonly IReadOnlyList<Account> All =
    [
        new("intake.alpha", "Practice Intake Alpha", [Domain.Roles.IntakeCoordinator]),
        new("intake.bravo", "Practice Intake Bravo", [Domain.Roles.IntakeCoordinator]),
        new("reviewer.charlie", "Practice Reviewer Charlie", [Domain.Roles.EligibilityReviewer]),
        new("scheduler.delta", "Practice Scheduler Delta", [Domain.Roles.SchedulingOfficer]),
        new("manager.echo", "Practice Manager Echo", [Domain.Roles.OperationsManager]),
        new("attendant.foxtrot", "Practice Attendant Foxtrot", [Domain.Roles.EventAttendant]),
        new("attendant.golf", "Practice Attendant Golf", [Domain.Roles.EventAttendant]),
        new("auditor.hotel", "Practice Auditor Hotel", [Domain.Roles.ComplianceAuditor]),
        new("admin.india", "Practice Admin India", [Domain.Roles.SystemAdmin]),
        new("dual.juliet", "Practice Reviewer-Manager Juliet", [Domain.Roles.EligibilityReviewer, Domain.Roles.OperationsManager]),
    ];
}
