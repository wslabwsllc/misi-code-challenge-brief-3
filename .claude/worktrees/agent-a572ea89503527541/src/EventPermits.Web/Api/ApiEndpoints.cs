using EventPermits.Application;

namespace EventPermits.Web.Api;

/// <summary>
/// Explicit command and query endpoints. Each resolves the actor from the database and calls the same application
/// service method the Blazor UI uses, so crafted-request tests exercise the UI's code path. No generic status setter.
/// </summary>
public static partial class ApiEndpoints
{
    public const string AntiforgeryHeader = "X-CSRF-TOKEN";

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter<AntiforgeryFilter>();
        api.MapGet("/antiforgery", AccountEndpoints.AntiforgeryToken);
        api.MapGet("/me", async (HttpContext http, ActorResolver actors, CancellationToken ct) =>
        {
            var actor = await actors.ResolveAsync(http.User, ct);
            return Results.Ok(new { actor.UserId, actor.DisplayName, roles = actor.Roles.Order() });
        });
        MapCaseEndpoints(api);
        MapSchedulingPageEndpoints(api);
    }

    static partial void MapCaseEndpoints(RouteGroupBuilder api);
}
