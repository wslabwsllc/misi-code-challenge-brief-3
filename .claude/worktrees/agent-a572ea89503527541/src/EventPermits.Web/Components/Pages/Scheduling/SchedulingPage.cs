using EventPermits.Domain;
using EventPermits.Web.Components.Shared;
using Microsoft.JSInterop;

namespace EventPermits.Web.Components.Pages.Scheduling;

/// <summary>
/// Shared plumbing for the scheduling and venue pages (07, 08, 09, 17, 21, 26): client-side checks that render exactly
/// like server <see cref="Application.ValidationException"/> field errors, focus on the first invalid field, and
/// small formatting helpers. Business rules stay in the application services.
/// </summary>
public abstract class SchedulingPage : AppPage
{
    private bool _focusFirstInvalid;

    /// <summary>Shows client-side field errors the same way as a 400 from the service, and focuses the first invalid field.</summary>
    protected void Fail(IReadOnlyDictionary<string, string[]> errors)
    {
        Problem = new UiProblem(400, "VALIDATION_FAILED", "Some fields need attention before this can be sent.", errors, null, 0, false, Correlation.BeginOperation());
        Announcement = null;
        _focusFirstInvalid = true;
    }

    protected string[] Errors(string field) => Problem?.For(field) ?? [];

    /// <summary>aria-describedby value: the hint id (if any) plus the error id when the field has errors.</summary>
    protected string? Describe(string field, string? hintId = null)
    {
        var ids = new List<string>();
        if (hintId is not null) ids.Add(hintId);
        if (Errors(field).Length > 0) ids.Add(ErrorId(field));
        return ids.Count == 0 ? null : string.Join(' ', ids);
    }

    protected static string ErrorId(string field) => "err-" + field.Replace('.', '-');

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        if (!_focusFirstInvalid) return;
        _focusFirstInvalid = false;
        try
        {
            await Js.InvokeVoidAsync("eventPermits.focusFirstInvalid");
        }
        catch (JSDisconnectedException)
        {
            // circuit closed; nothing to focus
        }
    }

    protected static string KindLabel(string kind) => kind switch
    {
        "reservation" => "Reservation",
        "external" => "Regional calendar event",
        "partner" => "Partner event",
        "blackout" => "Closure",
        _ => kind,
    };

    protected static string KindLabel(ConflictKind kind) => kind switch
    {
        ConflictKind.ReservationOverlap => "Reservation (setup and teardown included)",
        ConflictKind.Blackout => "Closure",
        ConflictKind.ExternalCalendarEvent => "Regional calendar event",
        ConflictKind.PartnerEvent => "Partner event",
        ConflictKind.VenueInactive => "Venue inactive",
        ConflictKind.CategoryNotAllowed => "Category not allowed",
        ConflictKind.OutsideOpeningHours => "Outside opening hours",
        ConflictKind.OverCapacity => "Over capacity",
        ConflictKind.InvalidAttendees => "Attendees",
        ConflictKind.InvalidDuration => "Duration",
        ConflictKind.ResourceShortage => "Resource shortage",
        ConflictKind.InPast => "In the past",
        _ => kind.ToString(),
    };

    protected static string KindClass(ConflictKind kind) => kind switch
    {
        ConflictKind.ReservationOverlap => "reservation",
        ConflictKind.Blackout => "blackout",
        ConflictKind.ExternalCalendarEvent => "external",
        ConflictKind.PartnerEvent => "partner",
        _ => "other",
    };

    protected static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", Ui.Culture);

    protected static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", Ui.Culture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    protected static TimeOnly? ParseTime(string? value) =>
        TimeOnly.TryParseExact(value, "HH:mm", Ui.Culture, System.Globalization.DateTimeStyles.None, out var t) ? t : null;

    /// <summary>The venue-local calendar date of a UTC instant.</summary>
    protected static DateOnly LocalDate(DateTimeOffset utc, string? timeZoneId) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, Ui.Zone(timeZoneId)).DateTime);

    protected static string CalendarStateText(CalendarPushState state) => state switch
    {
        CalendarPushState.NotPushed => "Not pushed",
        CalendarPushState.Pending => "Push queued",
        CalendarPushState.Pushed => "Hold accepted",
        CalendarPushState.Conflict => "Conflict — reconcile",
        CalendarPushState.Failed => "Push failed — will retry",
        _ => state.ToString(),
    };

    protected static string CalendarStateClass(CalendarPushState state) => state switch
    {
        CalendarPushState.Pushed => "chip--good",
        CalendarPushState.Conflict => "chip--bad",
        CalendarPushState.Failed => "chip--attention",
        _ => "chip--progress",
    };
}
