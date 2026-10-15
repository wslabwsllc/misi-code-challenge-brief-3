using EventPermits.Application;
using EventPermits.Web.Components.Shared;
using Microsoft.JSInterop;

namespace EventPermits.Web.Components.Pages.Decisions;

/// <summary>
/// Client-side field checks for the decision pages. They mirror the field limits the services enforce (the server
/// remains the authority and returns the same field keys), so errors render in the same place either way.
/// </summary>
public sealed class FieldChecks
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public FieldChecks Text(string field, string? value, int min, int max, string label, bool required = true)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            if (required) Add(field, $"{label} is required.");
            return this;
        }
        if (trimmed.Length < min || trimmed.Length > max)
            Add(field, min > 1 ? $"{label} must be {min} to {max} characters." : $"{label} must be at most {max} characters.");
        return this;
    }

    public FieldChecks Require(bool condition, string field, string message)
    {
        if (!condition) Add(field, message);
        return this;
    }

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = [];
        list.Add(message);
    }

    public UiProblem? ToProblem(string correlationId) => _errors.Count == 0
        ? null
        : new UiProblem(400, "VALIDATION_FAILED", "One or more fields are invalid.", _errors.ToDictionary(e => e.Key, e => e.Value.ToArray()), null, 0, false, correlationId);
}

/// <summary>Shared behaviour of the decision pages (05, 06, 10, 11, 20): client validation, field wiring, focus and conflict rows.</summary>
public abstract class DecisionPage : AppPage
{
    private bool _focusInvalid;
    private string? _focusSelector;

    /// <summary>Rows for the 409 conflict panel, set after a stale write.</summary>
    protected IReadOnlyList<ConflictPanel.Row>? ConflictRows { get; set; }

    /// <summary>Shows client-side field errors (same rendering as server errors) and focuses the first invalid field.</summary>
    protected bool ClientInvalid(FieldChecks checks)
    {
        var problem = checks.ToProblem(Correlation.BeginOperation());
        if (problem is null) return false;
        Problem = problem;
        Announcement = null;
        _focusInvalid = true;
        return true;
    }

    protected void FocusAfterRender(string selector) => _focusSelector = selector;

    /// <summary>aria-describedby value: the hint (when present) plus the error element when the field has errors.</summary>
    protected string? Describe(string field, string id, bool hint = false)
    {
        var ids = new List<string>();
        if (hint) ids.Add(id + "-hint");
        if (Problem?.For(field).Length > 0) ids.Add(id + "-error");
        return ids.Count == 0 ? null : string.Join(' ', ids);
    }

    protected IReadOnlyList<string> Errors(string field) => Problem?.For(field) ?? [];

    protected bool IsConflict => Problem?.StatusCode == 409;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        try
        {
            if (_focusInvalid)
            {
                _focusInvalid = false;
                await Js.InvokeVoidAsync("eventPermits.focusFirstInvalid");
            }
            else if (_focusSelector is { } selector)
            {
                _focusSelector = null;
                await Js.InvokeVoidAsync("eventPermits.focus", selector);
            }
        }
        catch (JSDisconnectedException)
        {
            // circuit closed; nothing to focus
        }
    }

    protected static string DocName(Domain.DocumentType type) => type switch
    {
        Domain.DocumentType.ProofOfAffiliation => "Proof of affiliation",
        Domain.DocumentType.EventPlan => "Event plan",
        Domain.DocumentType.InsuranceCertificate => "Insurance certificate",
        Domain.DocumentType.SitePlan => "Site plan",
        _ => "Other document",
    };
}
