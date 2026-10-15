using EventPermits.Application;
using EventPermits.Application.Services;
using EventPermits.Domain;
using Microsoft.AspNetCore.Mvc;

namespace EventPermits.Web.Api;

public sealed record VersionBody(uint? Version);
public sealed record ApplicantBody(uint? Version, bool Complete, string? FullName, string? Organization, string? Email, string? Phone, string? RegistryReference);
public sealed record EventBody(uint? Version, bool Complete, string? Title, Guid? CategoryId, Guid? VenueId, DateOnly? LocalDate, TimeOnly? LocalStart, int? DurationMinutes, int? AttendeeCount, string? Description);
public sealed record FacilitiesBody(uint? Version, bool Complete, List<ResourceRequestInput>? Resources, string? SpecialNeeds);
public sealed record CommentBody(uint? Version, string? Body);
public sealed record PassBody(uint? Version, Dictionary<string, bool>? Checklist, string? OverrideReason);
public sealed record ReasonBody(uint? Version, string? Reason);
public sealed record NotesBody(uint? Version, string? Notes);
public sealed record InfoRequestBody(uint? Version, List<string>? Items, string? Message, DateOnly? DueDate);
public sealed record RespondBody(uint? Version, string? Response);
public sealed record ReserveBody(uint? Version, Guid VenueId, DateOnly LocalDate, TimeOnly LocalStart, int DurationMinutes, int AttendeeCount, List<ResourceRequestInput>? Resources, string? AttendantNeeds);
public sealed record SlotBody(Guid VenueId, DateOnly LocalDate, TimeOnly LocalStart, int DurationMinutes, int AttendeeCount, string? CategoryCode, List<ResourceRequestInput>? Resources);
public sealed record AllocationBody(uint? Version, Guid ResourceId, int Quantity);
public sealed record IssueBody(uint? Version, string? Conditions);
public sealed record ReissueBody(uint? Version, string? Reason, string? Conditions);
public sealed record CodeBody(string? Code);
public sealed record CheckOutBody(uint? Version, string? Notes);
public sealed record AttendantBody(uint? Version, string UserId, bool Assign);
public sealed record ReadinessBody(uint? Version, bool Done);
public sealed record IncidentBody(uint? Version, string? Category, IncidentSeverity Severity, string? Description, string? OwnerUserId, DateOnly? DueDate, string? CorrectiveAction);
public sealed record VerifyBody(string? Reference, Guid? ApplicationId);
public sealed record AlternativeBody(uint? Version, DateTimeOffset StartUtc);
public sealed record ManualNotificationBody(Guid ApplicationId, string TemplateCode, string? Message);

public static partial class ApiEndpoints
{
    private static async Task<Actor> Me(HttpContext http, ActorResolver actors, CancellationToken ct) => await actors.ResolveAsync(http.User, ct);

    static partial void MapCaseEndpoints(RouteGroupBuilder api)
    {
        // ---------------- intake and register (FR-02, FR-03)
        api.MapGet("/lookups", async (HttpContext h, ActorResolver a, CaseService s, CancellationToken ct) => Results.Ok(await s.LookupsAsync(await Me(h, a, ct), ct)));
        api.MapGet("/applications", async ([AsParameters] RegisterParams p, HttpContext h, ActorResolver a, RegisterService s, CancellationToken ct) =>
            Results.Ok(await s.ListAsync(await Me(h, a, ct), p.ToQuery(), ct)));
        api.MapGet("/applications/export.csv", async ([AsParameters] RegisterParams p, HttpContext h, ActorResolver a, RegisterService s, CancellationToken ct) =>
            Results.File(await s.ExportCsvAsync(await Me(h, a, ct), p.ToQuery(), ct), "text/csv; charset=utf-8", $"applications-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv"));
        api.MapPost("/applications", async (HttpContext h, ActorResolver a, CaseService s, CancellationToken ct) =>
        {
            var key = h.Request.Headers["Idempotency-Key"].FirstOrDefault();
            var created = await s.CreateDraftAsync(await Me(h, a, ct), key, ct);
            return Results.Created($"/api/applications/{created.Id}", created);
        });
        api.MapGet("/applications/{id:guid}", async (Guid id, HttpContext h, ActorResolver a, CaseService s, CancellationToken ct) => Results.Ok(await s.GetAsync(await Me(h, a, ct), id, ct)));
        api.MapPut("/applications/{id:guid}/applicant", async (Guid id, ApplicantBody b, HttpContext h, ActorResolver a, CaseService s, CancellationToken ct) =>
            Results.Ok(await s.SaveApplicantAsync(await Me(h, a, ct), id, b.Version, new ApplicantInput(b.FullName, b.Organization, b.Email, b.Phone, b.RegistryReference), b.Complete, ct)));
        api.MapPut("/applications/{id:guid}/event", async (Guid id, EventBody b, HttpContext h, ActorResolver a, CaseService s, CancellationToken ct) =>
            Results.Ok(await s.SaveEventAsync(await Me(h, a, ct), id, b.Version, new EventInput(b.Title, b.CategoryId, b.VenueId, b.LocalDate, b.LocalStart, b.DurationMinutes, b.AttendeeCount, b.Description), b.Complete, ct)));
        api.MapPut("/applications/{id:guid}/facilities", async (Guid id, FacilitiesBody b, HttpContext h, ActorResolver a, CaseService s, CancellationToken ct) =>
            Results.Ok(await s.SaveFacilitiesAsync(await Me(h, a, ct), id, b.Version, new FacilitiesInput(b.Resources ?? [], b.SpecialNeeds), b.Complete, ct)));
        api.MapPost("/applications/{id:guid}/documents", async (Guid id, HttpContext h, ActorResolver a, CaseService s, CancellationToken ct) =>
        {
            var form = await h.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw ValidationException.For("file", "Choose a file to upload.");
            if (!Enum.TryParse<DocumentType>(form["documentType"], true, out var type)) throw ValidationException.For("documentType", "Choose the document type.");
            uint? version = uint.TryParse(form["version"], out var v) ? v : null;
            Guid? requestId = Guid.TryParse(form["informationRequestId"], out var rid) ? rid : null;
            return Results.Ok(await s.UploadDocumentAsync(await Me(h, a, ct), id, version, type, await Upload(file, ct), requestId, ct));
        }).DisableAntiforgery().AddEndpointFilter<AntiforgeryFilter>();
        api.MapPost("/applications/{id:guid}/submit", async (Guid id, VersionBody b, HttpContext h, ActorResolver a, CaseService s, CancellationToken ct) =>
            Results.Ok(await s.SubmitAsync(await Me(h, a, ct), id, b.Version, ct)));
        api.MapPost("/applications/{id:guid}/comments", async (Guid id, CommentBody b, HttpContext h, ActorResolver a, CaseService s, CancellationToken ct) =>
            Results.Ok(await s.AddCommentAsync(await Me(h, a, ct), id, b.Version, b.Body, ct)));

        // ---------------- eligibility and information requests (FR-04)
        api.MapGet("/reviews", async (string? search, int? page, HttpContext h, ActorResolver a, ReviewService s, CancellationToken ct) =>
        {
            var (rows, total) = await s.QueueAsync(await Me(h, a, ct), search, page ?? 1, ct);
            return Results.Ok(new { rows, total });
        });
        api.MapPost("/applications/{id:guid}/verify-registry", async (Guid id, HttpContext h, ActorResolver a, ReviewService s, CancellationToken ct) =>
            Results.Ok(await s.VerifyCaseAsync(await Me(h, a, ct), id, ct)));
        api.MapPost("/registry/verify", async (VerifyBody b, HttpContext h, ActorResolver a, ReviewService s, CancellationToken ct) =>
            Results.Ok(await s.VerifyAsync(await Me(h, a, ct), b.Reference ?? "", b.ApplicationId, ct)));
        api.MapPost("/applications/{id:guid}/eligibility/pass", async (Guid id, PassBody b, HttpContext h, ActorResolver a, ReviewService s, CancellationToken ct) =>
            Results.Ok(await s.PassAsync(await Me(h, a, ct), id, b.Version, b.Checklist ?? [], b.OverrideReason, ct)));
        api.MapPost("/applications/{id:guid}/eligibility/reject", async (Guid id, ReasonBody b, HttpContext h, ActorResolver a, ReviewService s, CancellationToken ct) =>
            Results.Ok(await s.RejectAsync(await Me(h, a, ct), id, b.Version, b.Reason, null, ct)));
        api.MapPost("/applications/{id:guid}/information-requests", async (Guid id, InfoRequestBody b, HttpContext h, ActorResolver a, ReviewService s, CancellationToken ct) =>
            Results.Ok(await s.RequestInformationAsync(await Me(h, a, ct), id, b.Version, b.Items ?? [], b.Message, b.DueDate, ct)));
        api.MapGet("/information-requests", async (InboxView? view, int? page, HttpContext h, ActorResolver a, ReviewService s, CancellationToken ct) =>
        {
            var (rows, total) = await s.InboxAsync(await Me(h, a, ct), view ?? InboxView.Open, page ?? 1, ct);
            return Results.Ok(new { rows, total });
        });
        api.MapPost("/information-requests/{id:guid}/respond", async (Guid id, RespondBody b, HttpContext h, ActorResolver a, ReviewService s, CancellationToken ct) =>
            Results.Ok(await s.RespondAsync(await Me(h, a, ct), id, b.Version, b.Response, ct)));
        api.MapPost("/applications/{id:guid}/resubmit", async (Guid id, VersionBody b, HttpContext h, ActorResolver a, ReviewService s, CancellationToken ct) =>
            Results.Ok(await s.ResubmitAsync(await Me(h, a, ct), id, b.Version, ct)));

        // ---------------- scheduling (FR-05..FR-07)
        api.MapPost("/schedule/check", async (SlotBody b, HttpContext h, ActorResolver a, SchedulingService s, CancellationToken ct) =>
            Results.Ok(await s.CheckAsync(await Me(h, a, ct), new SlotCheckInput(b.VenueId, b.LocalDate, b.LocalStart, b.DurationMinutes, b.AttendeeCount, b.CategoryCode, b.Resources, null), ct)));
        api.MapPost("/applications/{id:guid}/reservation", async (Guid id, ReserveBody b, HttpContext h, ActorResolver a, SchedulingService s, CancellationToken ct) =>
            Results.Ok(await s.ReserveAsync(await Me(h, a, ct), id, b.Version, new ReserveInput(b.VenueId, b.LocalDate, b.LocalStart, b.DurationMinutes, b.AttendeeCount, b.Resources ?? [], b.AttendantNeeds), ct)));
        api.MapPost("/applications/{id:guid}/submit-for-approval", async (Guid id, VersionBody b, HttpContext h, ActorResolver a, SchedulingService s, CancellationToken ct) =>
            Results.Ok(await s.SubmitForApprovalAsync(await Me(h, a, ct), id, b.Version, ct)));
        api.MapPut("/reservations/{id:guid}/allocations", async (Guid id, AllocationBody b, HttpContext h, ActorResolver a, SchedulingService s, CancellationToken ct) =>
            Results.Ok(await s.SetAllocationAsync(await Me(h, a, ct), id, b.ResourceId, b.Quantity, b.Version, ct)));
        api.MapGet("/calendar", async (DateTimeOffset from, DateTimeOffset to, Guid? cemeteryId, Guid? venueId, HttpContext h, ActorResolver a, SchedulingService s, CancellationToken ct) =>
            Results.Ok(await s.CalendarAsync(await Me(h, a, ct), from, to, cemeteryId, venueId, ct)));
        api.MapPost("/calendar/sync", async (HttpContext h, ActorResolver a, CalendarSyncService s, CancellationToken ct) => Results.Ok(await s.SyncNowAsync(await Me(h, a, ct), ct)));
        api.MapPost("/reservations/{id:guid}/push", async (Guid id, HttpContext h, ActorResolver a, CalendarSyncService s, CancellationToken ct) =>
        {
            await s.RetryPushAsync(await Me(h, a, ct), id, ct);
            return Results.NoContent();
        });
        api.MapPost("/reconciliation/{id:guid}/alternative", async (Guid id, AlternativeBody b, HttpContext h, ActorResolver a, CalendarSyncService s, CancellationToken ct) =>
        {
            await s.ChooseAlternativeAsync(await Me(h, a, ct), id, b.StartUtc, b.Version, ct);
            return Results.NoContent();
        });

        // ---------------- approval and permits (FR-08, FR-09)
        api.MapGet("/approvals", async (HttpContext h, ActorResolver a, ApprovalService s, CancellationToken ct) => Results.Ok(await s.QueueAsync(await Me(h, a, ct), ct)));
        api.MapPost("/applications/{id:guid}/approve", async (Guid id, NotesBody b, HttpContext h, ActorResolver a, ApprovalService s, CancellationToken ct) =>
            Results.Ok(await s.ApproveAsync(await Me(h, a, ct), id, b.Version, b.Notes, ct)));
        api.MapPost("/applications/{id:guid}/reject", async (Guid id, ReasonBody b, HttpContext h, ActorResolver a, ApprovalService s, CancellationToken ct) =>
            Results.Ok(await s.RejectAsync(await Me(h, a, ct), id, b.Version, b.Reason, ct)));
        api.MapPost("/applications/{id:guid}/acknowledge-weather", async (Guid id, NotesBody b, HttpContext h, ActorResolver a, ApprovalService s, CancellationToken ct) =>
            Results.Ok(await s.AcknowledgeWeatherAsync(await Me(h, a, ct), id, b.Version, b.Notes, ct)));
        api.MapPost("/applications/{id:guid}/permit", async (Guid id, IssueBody b, HttpContext h, ActorResolver a, PermitService s, CancellationToken ct) =>
            Results.Ok(await s.IssueAsync(await Me(h, a, ct), id, b.Version, b.Conditions, ct)));
        api.MapPost("/applications/{id:guid}/permit/reissue", async (Guid id, ReissueBody b, HttpContext h, ActorResolver a, PermitService s, CancellationToken ct) =>
            Results.Ok(await s.ReissueAsync(await Me(h, a, ct), id, b.Version, b.Reason, b.Conditions, ct)));
        api.MapPost("/applications/{id:guid}/permit/revoke", async (Guid id, ReasonBody b, HttpContext h, ActorResolver a, PermitService s, CancellationToken ct) =>
            Results.Ok(await s.RevokeAsync(await Me(h, a, ct), id, b.Version, b.Reason, ct)));
        api.MapGet("/permits", async (string? search, PermitStatus? status, int? page, HttpContext h, ActorResolver a, PermitService s, CancellationToken ct) =>
        {
            var (rows, total) = await s.ListAsync(await Me(h, a, ct), search, status, page ?? 1, ct);
            return Results.Ok(new { rows, total });
        });
        api.MapPost("/permits/verify", async (CodeBody b, HttpContext h, ActorResolver a, PermitService s, CancellationToken ct) => Results.Ok(await s.VerifyAsync(await Me(h, a, ct), b.Code, ct)));

        // ---------------- event day (FR-10, FR-11)
        api.MapGet("/roster", async (DateOnly date, Guid? cemeteryId, HttpContext h, ActorResolver a, OperationsService s, CancellationToken ct) =>
            Results.Ok(await s.RosterAsync(await Me(h, a, ct), date, cemeteryId, ct)));
        api.MapPost("/applications/{id:guid}/attendants", async (Guid id, AttendantBody b, HttpContext h, ActorResolver a, OperationsService s, CancellationToken ct) =>
            Results.Ok(await s.AssignAttendantAsync(await Me(h, a, ct), id, b.Version, b.UserId, b.Assign, ct)));
        api.MapPut("/readiness/{id:guid}", async (Guid id, ReadinessBody b, HttpContext h, ActorResolver a, OperationsService s, CancellationToken ct) =>
            Results.Ok(await s.SetReadinessAsync(await Me(h, a, ct), id, b.Done, b.Version, ct)));
        api.MapPost("/check-in", async (CodeBody b, HttpContext h, ActorResolver a, OperationsService s, CancellationToken ct) => Results.Ok(await s.CheckInAsync(await Me(h, a, ct), b.Code, ct)));
        api.MapPost("/applications/{id:guid}/check-out", async (Guid id, CheckOutBody b, HttpContext h, ActorResolver a, OperationsService s, CancellationToken ct) =>
            Results.Ok(await s.CheckOutAsync(await Me(h, a, ct), id, b.Version, b.Notes, ct)));
        api.MapPost("/applications/{id:guid}/complete", async (Guid id, VersionBody b, HttpContext h, ActorResolver a, OperationsService s, CancellationToken ct) =>
            Results.Ok(await s.CompleteAsync(await Me(h, a, ct), id, b.Version, ct)));
        api.MapPost("/applications/{id:guid}/close", async (Guid id, NotesBody b, HttpContext h, ActorResolver a, OperationsService s, CancellationToken ct) =>
            Results.Ok(await s.CloseAsync(await Me(h, a, ct), id, b.Version, b.Notes, ct)));
        api.MapPost("/applications/{id:guid}/cancel", async (Guid id, ReasonBody b, HttpContext h, ActorResolver a, OperationsService s, CancellationToken ct) =>
            Results.Ok(await s.CancelAsync(await Me(h, a, ct), id, b.Version, b.Reason, ct)));
        api.MapGet("/incidents", async ([AsParameters] IncidentParams p, HttpContext h, ActorResolver a, IncidentService s, CancellationToken ct) =>
        {
            var (rows, total) = await s.ListAsync(await Me(h, a, ct), new IncidentFilter(p.Severity, p.Status, p.CemeteryId, p.From, p.To, p.Page ?? 1), ct);
            return Results.Ok(new { rows, total });
        });
        api.MapPost("/applications/{id:guid}/incidents", async (Guid id, IncidentBody b, HttpContext h, ActorResolver a, IncidentService s, CancellationToken ct) =>
        {
            var incidentId = await s.CreateAsync(await Me(h, a, ct), id, b.Version, new IncidentInput(b.Category, b.Severity, b.Description, b.OwnerUserId, b.DueDate, b.CorrectiveAction), ct);
            return Results.Created($"/api/incidents/{incidentId}", new { id = incidentId });
        });
        api.MapPut("/incidents/{id:guid}", async (Guid id, IncidentBody b, HttpContext h, ActorResolver a, IncidentService s, CancellationToken ct) =>
        {
            await s.UpdateAsync(await Me(h, a, ct), id, b.Version, new IncidentInput(b.Category, b.Severity, b.Description, b.OwnerUserId, b.DueDate, b.CorrectiveAction), ct);
            return Results.NoContent();
        });
        api.MapPost("/incidents/{id:guid}/escalate", async (Guid id, ReasonBody b, HttpContext h, ActorResolver a, IncidentService s, CancellationToken ct) =>
        {
            await s.EscalateAsync(await Me(h, a, ct), id, b.Version, b.Reason, ct);
            return Results.NoContent();
        });
        api.MapPost("/incidents/{id:guid}/resolve", async (Guid id, ReasonBody b, HttpContext h, ActorResolver a, IncidentService s, CancellationToken ct) =>
        {
            await s.ResolveAsync(await Me(h, a, ct), id, b.Version, b.Reason, ct);
            return Results.NoContent();
        });
        api.MapPost("/incidents/{id:guid}/photos", async (Guid id, HttpContext h, ActorResolver a, IncidentService s, CancellationToken ct) =>
        {
            var form = await h.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw ValidationException.For("file", "Choose a photo to upload.");
            uint? version = uint.TryParse(form["version"], out var v) ? v : null;
            await s.AddPhotoAsync(await Me(h, a, ct), id, version, await Upload(file, ct), ct);
            return Results.NoContent();
        }).DisableAntiforgery().AddEndpointFilter<AntiforgeryFilter>();

        // ---------------- files and notifications (FR-12, FR-15)
        api.MapPost("/attachments/{id:guid}/link", async (Guid id, HttpContext h, ActorResolver a, FileService s, CancellationToken ct) => Results.Ok(await s.IssueLinkAsync(await Me(h, a, ct), id, ct)));
        api.MapGet("/notifications", async (NotificationStatus? status, string? search, int? page, HttpContext h, ActorResolver a, NotificationService s, CancellationToken ct) =>
        {
            var (rows, total) = await s.ListAsync(await Me(h, a, ct), new NotificationFilter(status, search, page ?? 1), ct);
            return Results.Ok(new { rows, total });
        });
        api.MapPost("/notifications/{id:guid}/retry", async (Guid id, VersionBody b, HttpContext h, ActorResolver a, NotificationService s, CancellationToken ct) =>
        {
            await s.RetryAsync(await Me(h, a, ct), id, b.Version, ct);
            return Results.NoContent();
        });
        api.MapPost("/notifications", async (ManualNotificationBody b, HttpContext h, ActorResolver a, NotificationService s, CancellationToken ct) =>
        {
            await s.SendManualAsync(await Me(h, a, ct), b.ApplicationId, b.TemplateCode, b.Message, ct);
            return Results.Accepted();
        });
        MapAdminEndpoints(api);
    }

    static partial void MapAdminEndpoints(RouteGroupBuilder api);

    private static async Task<UploadFile> Upload(IFormFile file, CancellationToken ct)
    {
        if (file.Length > Limits.MaxAttachmentBytes) throw new BusinessRuleException(ErrorCodes.AttachmentRejected, $"The file is larger than 5 MiB ({Limits.MaxAttachmentBytes:N0} bytes).");
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        return new UploadFile(file.FileName, file.ContentType, buffer.ToArray());
    }
}

public sealed class RegisterParams
{
    [FromQuery] public string? Search { get; init; }
    [FromQuery] public string? Status { get; init; }
    [FromQuery] public Guid? CemeteryId { get; init; }
    [FromQuery] public Guid? VenueId { get; init; }
    [FromQuery] public Guid? CategoryId { get; init; }
    [FromQuery] public string? Applicant { get; init; }
    [FromQuery] public DateOnly? From { get; init; }
    [FromQuery] public DateOnly? To { get; init; }
    [FromQuery] public RegisterSort? Sort { get; init; }
    [FromQuery] public bool? Desc { get; init; }
    [FromQuery] public int? Page { get; init; }
    [FromQuery] public bool? MyDrafts { get; init; }

    public RegisterQuery ToQuery()
    {
        var statuses = (Status ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<CaseStatus>(s, true, out var st) ? st : throw ValidationException.For("status", $"Unknown status '{s}'.")).ToArray();
        return new RegisterQuery(Search, statuses, CemeteryId, VenueId, CategoryId, Applicant, From, To, Sort ?? RegisterSort.Updated, Desc ?? true, Page ?? 1, MyDrafts ?? false);
    }
}

public sealed class IncidentParams
{
    [FromQuery] public IncidentSeverity? Severity { get; init; }
    [FromQuery] public IncidentStatus? Status { get; init; }
    [FromQuery] public Guid? CemeteryId { get; init; }
    [FromQuery] public DateOnly? From { get; init; }
    [FromQuery] public DateOnly? To { get; init; }
    [FromQuery] public int? Page { get; init; }
}
