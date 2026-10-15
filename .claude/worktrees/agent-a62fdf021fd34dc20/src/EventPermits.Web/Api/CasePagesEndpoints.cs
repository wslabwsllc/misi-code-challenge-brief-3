using EventPermits.Application;
using EventPermits.Application.Services;
using EventPermits.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EventPermits.Web.Api;

public sealed record SavedViewBody(uint? Version, string? Name, string? Query);

/// <summary>
/// Endpoints behind pages 02, 03, 04 and 23 that the first API pass did not expose: saved register views, the
/// Document Vault list, per-file audit history, upload targets, submission readiness and inline file previews.
/// Each calls the same application-service method as the page.
/// </summary>
public static class CasePagesEndpoints
{
    public static void MapCasePagesEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter<AntiforgeryFilter>();
        static Task<Actor> Me(HttpContext h, ActorResolver a, CancellationToken ct) => a.ResolveAsync(h.User, ct);

        // ---------------- saved register views (E4-02 AC3, A-23)
        api.MapGet("/register/views", async (HttpContext h, ActorResolver a, RegisterService s, CancellationToken ct) =>
            Results.Ok(await s.ViewsAsync(await Me(h, a, ct), ct)));
        api.MapPost("/register/views", async (SavedViewBody b, HttpContext h, ActorResolver a, RegisterService s, CancellationToken ct) =>
        {
            var view = await s.SaveViewAsync(await Me(h, a, ct), null, b.Name, b.Query, null, ct);
            return Results.Created($"/api/register/views/{view.Id}", view);
        });
        api.MapPut("/register/views/{id:guid}", async (Guid id, SavedViewBody b, HttpContext h, ActorResolver a, RegisterService s, CancellationToken ct) =>
            Results.Ok(await s.SaveViewAsync(await Me(h, a, ct), id, b.Name, b.Query, b.Version, ct)));
        api.MapDelete("/register/views/{id:guid}", async (Guid id, HttpContext h, ActorResolver a, RegisterService s, CancellationToken ct) =>
        {
            await s.DeleteViewAsync(await Me(h, a, ct), id, ct);
            return Results.NoContent();
        });

        // ---------------- intake helpers (E4-01)
        api.MapGet("/applications/upload-targets", async (HttpContext h, ActorResolver a, CaseService s, CancellationToken ct) =>
            Results.Ok(await s.UploadTargetsAsync(await Me(h, a, ct), ct)));
        api.MapGet("/applications/{id:guid}/submission-problems", async (Guid id, HttpContext h, ActorResolver a, CaseService s, CancellationToken ct) =>
            Results.Ok(await s.SubmissionProblemsAsync(await Me(h, a, ct), id, ct)));
        api.MapGet("/applications/find", async (string? reference, HttpContext h, ActorResolver a, CaseService s, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(reference)) throw ValidationException.For("reference", "Enter a case reference.");
            return await s.FindAsync(await Me(h, a, ct), reference, ct) is { } id ? Results.Ok(new { id }) : throw new NotFoundException();
        });

        // ---------------- document vault (E3-05)
        api.MapGet("/documents", async ([AsParameters] VaultParams p, HttpContext h, ActorResolver a, FileService s, CancellationToken ct) =>
        {
            var (rows, total) = await s.VaultPageAsync(await Me(h, a, ct), p.ToQuery(), ct);
            return Results.Ok(new { rows, total });
        });
        api.MapGet("/attachments/{id:guid}/history", async (Guid id, HttpContext h, ActorResolver a, FileService s, CancellationToken ct) =>
            Results.Ok(await s.HistoryAsync(await Me(h, a, ct), id, ct)));

        // Inline preview of the same signed app link (A-16), served from this origin so images and PDFs can be shown in the page.
        app.MapGet("/files/{id:guid}/preview", async (Guid id, string? exp, string? sig, HttpContext http, ActorResolver actors, FileService files, CancellationToken ct) =>
        {
            if (http.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            try
            {
                var preview = await files.PreviewAsync(await actors.ResolveAsync(http.User, ct), id, exp, sig, ct);
                http.Response.Headers.ContentDisposition = $"inline; filename=\"{preview.FileName.Replace("\"", "")}\"";
                http.Response.Headers.CacheControl = "no-store";
                return Results.Bytes(preview.Content, preview.ContentType);
            }
            catch (ForbiddenException ex) when (ex.Code is ErrorCodes.LinkExpired or ErrorCodes.LinkInvalid)
            {
                return Results.Redirect($"/files/expired?id={id}&reason={(ex.Code == ErrorCodes.LinkExpired ? "expired" : "invalid")}");
            }
        });
    }
}

public sealed class VaultParams
{
    [FromQuery] public string? Search { get; init; }
    [FromQuery(Name = "case")] public string? Case { get; init; }
    [FromQuery] public string? Uploader { get; init; }
    [FromQuery] public AttachmentPurpose? Purpose { get; init; }
    [FromQuery] public DocumentType? Type { get; init; }
    [FromQuery] public DateOnly? From { get; init; }
    [FromQuery] public DateOnly? To { get; init; }
    [FromQuery] public int? Page { get; init; }

    public VaultQuery ToQuery() => new(Search, Case, Uploader, Purpose, Type, From, To, Page ?? 1);
}
