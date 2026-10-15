using EventPermits.Application;
using EventPermits.Web.Components.Shared;
using Microsoft.JSInterop;

namespace EventPermits.Web.Components.Pages.Cases;

/// <summary>
/// Field-error helpers shared by the case pages (02, 03, 04, 23): messages come from the server's
/// <see cref="ValidationException"/> field errors, or from a quick local check before a round trip.
/// </summary>
public abstract class CasesPage : AppPage
{
    private bool _focusLocal;

    /// <summary>Messages for one field (server field errors keyed by the same name the service validates).</summary>
    protected IReadOnlyList<string> Errs(string field) => Problem?.For(field) ?? [];

    /// <summary>aria-describedby value: the hint id (if any) plus the error id when the field has an error.</summary>
    protected string? Describe(string field, string? hintId = null)
    {
        var ids = new List<string>();
        if (hintId is not null) ids.Add(hintId);
        if (Errs(field).Count > 0) ids.Add($"{field}-error");
        return ids.Count == 0 ? null : string.Join(' ', ids);
    }

    /// <summary>A failed local check shown exactly like a server validation error (focus moves to the field).</summary>
    protected void LocalError(string field, string message)
    {
        Announcement = null;
        Problem = new UiProblem(400, "VALIDATION_FAILED", "One or more fields are invalid.",
            new Dictionary<string, string[]> { [field] = [message] }, null, 0, false, Correlation.BeginOperation());
        _focusLocal = true;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        if (!_focusLocal) return;
        _focusLocal = false;
        try
        {
            await Js.InvokeVoidAsync("eventPermits.focusFirstInvalid");
        }
        catch (JSDisconnectedException)
        {
            // circuit closed
        }
    }

    protected static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.0} MiB",
        >= 1024 => $"{bytes / 1024d:0.0} KiB",
        _ => $"{bytes} bytes",
    };
}
