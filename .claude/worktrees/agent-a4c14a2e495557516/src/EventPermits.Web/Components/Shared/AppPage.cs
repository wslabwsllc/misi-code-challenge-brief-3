using EventPermits.Application;
using EventPermits.Web.Hosting;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using Serilog.Context;

namespace EventPermits.Web.Components.Shared;

public sealed record UiProblem(
    int StatusCode,
    string Code,
    string Title,
    IReadOnlyDictionary<string, string[]> Errors,
    object? Current,
    uint CurrentVersion,
    bool Retryable,
    string CorrelationId)
{
    public string[] For(string field) => Errors.TryGetValue(field, out var e) ? e : [];

    public static UiProblem From(AppException ex, string correlationId) => new(
        ex.StatusCode, ex.Code, ex.Message,
        ex is ValidationException v ? v.Errors : new Dictionary<string, string[]>(),
        (ex as ConflictException)?.Current,
        (ex as ConflictException)?.CurrentVersion ?? 0,
        ex is DependencyUnavailableException,
        correlationId);
}

/// <summary>
/// Base for interactive pages. Every action resolves the actor from the database (not the circuit), starts a new
/// correlation ID, and turns application errors into an announced inline problem (E0-04, E1-03, E2-05).
/// </summary>
public abstract class AppPage : ComponentBase
{
    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }
    [Inject] protected ActorResolver Actors { get; set; } = default!;
    [Inject] protected CorrelationContext Correlation { get; set; } = default!;
    [Inject] protected IJSRuntime Js { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] protected TimeProvider Clock { get; set; } = default!;

    protected UiProblem? Problem { get; set; }
    protected string? Announcement { get; set; }
    protected bool Busy { get; set; }
    protected bool Loading { get; set; } = true;
    protected Actor? Me { get; private set; }
    private bool _focusInvalid;

    protected async Task<Actor> GetActorAsync()
    {
        var state = await (AuthState ?? throw new InvalidOperationException("No authentication state."));
        Me = await Actors.ResolveAsync(state.User);
        return Me;
    }

    /// <summary>Loads page data; problems (403/404/503) render inline instead of crashing the circuit.</summary>
    protected async Task LoadAsync(Func<Actor, Task> load)
    {
        Loading = true;
        var id = Correlation.BeginOperation();
        try
        {
            var actor = await GetActorAsync();
            using (LogContext.PushProperty("CorrelationId", id))
                await load(actor);
        }
        catch (AppException ex)
        {
            Problem = UiProblem.From(ex, id);
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>Runs one user action. Returns true on success and announces <paramref name="success"/>.</summary>
    protected async Task<bool> RunAsync(Func<Actor, Task> action, string? success = null)
    {
        if (Busy) return false;
        Busy = true;
        Problem = null;
        Announcement = null;
        var id = Correlation.BeginOperation();
        try
        {
            var actor = await GetActorAsync();
            using (LogContext.PushProperty("CorrelationId", id))
            using (LogContext.PushProperty("Actor", actor.UserId))
            {
                await action(actor);
            }
            Announcement = success;
            return true;
        }
        catch (AppException ex)
        {
            Problem = UiProblem.From(ex, id);
            _focusInvalid = ex is ValidationException;
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusInvalid)
        {
            _focusInvalid = false;
            try
            {
                await Js.InvokeVoidAsync("eventPermits.focusFirstInvalid");
            }
            catch (JSDisconnectedException)
            {
                // circuit closed; nothing to focus
            }
        }
    }

    protected string? Invalid(string field) => Problem?.For(field).Length > 0 ? "true" : null;

    protected string Local(DateTimeOffset utc, string? timeZoneId, string format = "ddd d MMM yyyy, HH:mm") => Ui.Local(utc, timeZoneId, format);
}
