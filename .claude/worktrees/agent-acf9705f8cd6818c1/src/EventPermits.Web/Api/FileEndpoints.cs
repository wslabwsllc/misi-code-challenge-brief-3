using EventPermits.Application;
using EventPermits.Application.Services;

namespace EventPermits.Web.Api;

public static class FileEndpoints
{
    /// <summary>
    /// Redeems a signed app link (A-16): re-checks the user's access and the expiry, audits the download, then
    /// redirects to a 60-second storage URL. Expired or invalid links show a friendly page with "Regenerate link".
    /// </summary>
    public static void MapFileEndpoints(this WebApplication app)
    {
        app.MapGet("/files/{id:guid}", async (Guid id, string? exp, string? sig, HttpContext http, ActorResolver actors, FileService files, CancellationToken ct) =>
        {
            if (http.User.Identity?.IsAuthenticated != true)
                return Results.Redirect($"/account/login?returnUrl={Uri.EscapeDataString(http.Request.Path + http.Request.QueryString)}");
            try
            {
                var actor = await actors.ResolveAsync(http.User, ct);
                return Results.Redirect(await files.RedeemAsync(actor, id, exp, sig, ct));
            }
            catch (ForbiddenException ex) when (ex.Code is ErrorCodes.LinkExpired or ErrorCodes.LinkInvalid)
            {
                return Results.Redirect($"/files/expired?id={id}&reason={(ex.Code == ErrorCodes.LinkExpired ? "expired" : "invalid")}");
            }
        });
    }
}
