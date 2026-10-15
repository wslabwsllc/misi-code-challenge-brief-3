using System.Globalization;
using System.Reflection;
using EventPermits.Domain;

namespace EventPermits.Web.Components;

/// <summary>Formatting helpers shared by pages: venue-local times with zone labels, status chip styles.</summary>
public static class Ui
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public static TimeZoneInfo Zone(string? timeZoneId)
    {
        try
        {
            return string.IsNullOrWhiteSpace(timeZoneId) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Venue-local time with its zone abbreviation, e.g. "Tue 20 Oct 2026, 10:00 (America/New_York)".</summary>
    public static string Local(DateTimeOffset utc, string? timeZoneId, string format = "ddd d MMM yyyy, HH:mm")
    {
        var local = TimeZoneInfo.ConvertTime(utc, Zone(timeZoneId));
        return local.ToString(format, Culture) + (string.IsNullOrWhiteSpace(timeZoneId) ? " UTC" : $" ({ShortZone(timeZoneId)})");
    }

    public static string LocalTime(DateTimeOffset utc, string? timeZoneId) => TimeZoneInfo.ConvertTime(utc, Zone(timeZoneId)).ToString("HH:mm", Culture);

    public static string Utc(DateTimeOffset utc) => utc.UtcDateTime.ToString("yyyy-MM-dd HH:mm", Culture) + " UTC";

    public static string ShortZone(string? timeZoneId) => timeZoneId?.Split('/').LastOrDefault()?.Replace('_', ' ') ?? "UTC";

    public static string StatusClass(CaseStatus status) => status switch
    {
        CaseStatus.Draft => "chip--draft",
        CaseStatus.Submitted or CaseStatus.Eligible or CaseStatus.TentativelyReserved or CaseStatus.PendingApproval => "chip--progress",
        CaseStatus.NeedsInformation => "chip--attention",
        CaseStatus.Approved or CaseStatus.Permitted or CaseStatus.CheckedIn => "chip--brand",
        CaseStatus.EventCompleted or CaseStatus.Closed => "chip--good",
        CaseStatus.Rejected or CaseStatus.Cancelled => "chip--bad",
        _ => "chip--draft",
    };

    public static string SeverityClass(IncidentSeverity s) => s switch
    {
        IncidentSeverity.Critical or IncidentSeverity.High => "chip--bad",
        IncidentSeverity.Medium => "chip--attention",
        _ => "chip--draft",
    };

    public static string Plural(int n, string one, string many) => $"{n.ToString("N0", Culture)} {(n == 1 ? one : many)}";
}

public static class AppVersion
{
    public static readonly string Current =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+') switch
        {
            [var v, var sha, ..] => $"{v} ({sha[..Math.Min(8, sha.Length)]})",
            [var v] => v,
            _ => "dev",
        };
}
