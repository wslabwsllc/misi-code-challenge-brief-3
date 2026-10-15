using EventPermits.Application;
using EventPermits.Application.Services;
using EventPermits.Domain;

namespace EventPermits.Web.Api;

public sealed record NewUserBody(string? UserName, string? DisplayName, string? Email, List<string>? Roles, string? InitialPassword);
public sealed record ActiveBody(bool Active, string? Reason);
public sealed record RoleBody(string Role, bool Grant, string? Reason);
public sealed record RuleBody(uint? Version, string? Value);
public sealed record TemplateBody(uint? Version, string? Subject, string? Body, bool IsActive);
public sealed record VenueBody(uint? Version, Guid CemeteryId, string? Code, string? Name, string? Address, int Capacity, int SetupBufferMinutes, int TeardownBufferMinutes,
    List<OpeningHours>? OpeningHours, List<string>? AllowedCategoryCodes, bool IsActive);
public sealed record BlackoutBody(Guid VenueId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string? Reason);
public sealed record ResourceBody(uint? Version, Guid CemeteryId, string? Code, string? Name, string? Unit, int TotalQuantity, bool IsActive);
public sealed record CategoryBody(uint? Version, string? Code, string? Name, string? Description, List<DocumentType>? RequiredDocuments, List<string>? ReviewChecklist, bool IsActive);
public sealed record ScenarioBody(string Mode);
public sealed record ConfirmGeocodeBody(uint? Version, Guid CandidateId);
public sealed record DropBody(string Kind);
public sealed record ReconcileBody(string? Reason);

public static partial class ApiEndpoints
{
    static partial void MapAdminEndpoints(RouteGroupBuilder api)
    {
        // ---------------- users, rules, templates, audit (E1-05)
        api.MapGet("/admin/users", async (string? search, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) => Results.Ok(await s.UsersAsync(await Me(h, a, ct), search, ct)));
        api.MapPost("/admin/users", async (NewUserBody b, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
        {
            var id = await s.CreateUserAsync(await Me(h, a, ct), new NewUserInput(b.UserName, b.DisplayName, b.Email, b.Roles ?? [], b.InitialPassword), ct);
            return Results.Created($"/api/admin/users/{id}", new { id });
        });
        api.MapPost("/admin/users/{id}/active", async (string id, ActiveBody b, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
        {
            await s.SetActiveAsync(await Me(h, a, ct), id, b.Active, b.Reason, ct);
            return Results.NoContent();
        });
        api.MapPost("/admin/users/{id}/roles", async (string id, RoleBody b, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
        {
            await s.SetRoleAsync(await Me(h, a, ct), id, b.Role, b.Grant, b.Reason, ct);
            return Results.NoContent();
        });
        api.MapGet("/admin/rules", async (HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) => Results.Ok(await s.RulesAsync(await Me(h, a, ct), ct)));
        api.MapPut("/admin/rules/{id:guid}", async (Guid id, RuleBody b, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
        {
            await s.UpdateRuleAsync(await Me(h, a, ct), id, b.Version, b.Value, ct);
            return Results.NoContent();
        });
        api.MapGet("/admin/templates", async (HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) => Results.Ok(await s.TemplatesAsync(await Me(h, a, ct), ct)));
        api.MapPut("/admin/templates/{id:guid}", async (Guid id, TemplateBody b, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
        {
            await s.UpdateTemplateAsync(await Me(h, a, ct), id, b.Version, b.Subject, b.Body, b.IsActive, ct);
            return Results.NoContent();
        });
        api.MapGet("/audit", async ([AsParameters] AuditParams p, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
        {
            var (rows, total) = await s.AuditAsync(await Me(h, a, ct), p.ToFilter(), ct);
            return Results.Ok(new { rows, total });
        });
        api.MapGet("/audit/export.csv", async ([AsParameters] AuditParams p, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
            Results.File(await s.AuditCsvAsync(await Me(h, a, ct), p.ToFilter(), ct), "text/csv; charset=utf-8", $"audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv"));

        // ---------------- venues, closures, stock, categories (E6-01)
        api.MapGet("/admin/venues", async (HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) => Results.Ok(await s.VenuesAsync(await Me(h, a, ct), ct)));
        api.MapPost("/admin/venues", async (VenueBody b, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
            Results.Ok(await s.SaveVenueAsync(await Me(h, a, ct), null, null, Venue(b), ct)));
        api.MapPut("/admin/venues/{id:guid}", async (Guid id, VenueBody b, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
            Results.Ok(await s.SaveVenueAsync(await Me(h, a, ct), id, b.Version, Venue(b), ct)));
        api.MapGet("/admin/blackouts", async (Guid? venueId, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) => Results.Ok(await s.BlackoutsAsync(await Me(h, a, ct), venueId, ct)));
        api.MapPost("/admin/blackouts", async (BlackoutBody b, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
            Results.Ok(await s.AddBlackoutAsync(await Me(h, a, ct), new BlackoutInput(b.VenueId, b.StartUtc, b.EndUtc, b.Reason), ct)));
        api.MapPost("/admin/blackouts/{id:guid}/active", async (Guid id, uint? version, bool active, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
        {
            await s.SetBlackoutActiveAsync(await Me(h, a, ct), id, version, active, ct);
            return Results.NoContent();
        });
        api.MapGet("/admin/resources", async (HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) => Results.Ok(await s.ResourcesAsync(await Me(h, a, ct), ct)));
        api.MapPost("/admin/resources", async (ResourceBody b, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
        {
            await s.SaveResourceAsync(await Me(h, a, ct), null, null, b.CemeteryId, b.Code, b.Name, b.Unit, b.TotalQuantity, b.IsActive, ct);
            return Results.NoContent();
        });
        api.MapPut("/admin/resources/{id:guid}", async (Guid id, ResourceBody b, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
        {
            await s.SaveResourceAsync(await Me(h, a, ct), id, b.Version, b.CemeteryId, b.Code, b.Name, b.Unit, b.TotalQuantity, b.IsActive, ct);
            return Results.NoContent();
        });
        api.MapGet("/admin/categories", async (HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) => Results.Ok(await s.CategoriesAsync(await Me(h, a, ct), ct)));
        api.MapPut("/admin/categories/{id:guid}", async (Guid id, CategoryBody b, HttpContext h, ActorResolver a, AdminService s, CancellationToken ct) =>
        {
            await s.SaveCategoryAsync(await Me(h, a, ct), id, b.Version, b.Code, b.Name, b.Description, b.RequiredDocuments ?? [], b.ReviewChecklist ?? [], b.IsActive, ct);
            return Results.NoContent();
        });

        // ---------------- integration hub, weather, geocoding, imports (E3-03, E7-05, E6-08, E10)
        api.MapGet("/integrations", async (HttpContext h, ActorResolver a, IntegrationHubService s, CancellationToken ct) => Results.Ok(await s.CardsAsync(await Me(h, a, ct), ct)));
        api.MapPost("/integrations/{kind}/test", async (IntegrationKind kind, HttpContext h, ActorResolver a, IntegrationHubService s, CancellationToken ct) => Results.Ok(await s.TestAsync(await Me(h, a, ct), kind, ct)));
        api.MapGet("/integrations/scenarios", async (HttpContext h, ActorResolver a, IntegrationHubService s, CancellationToken ct) => Results.Ok(await s.ScenariosAsync(await Me(h, a, ct), ct)));
        api.MapPut("/integrations/{kind}/scenario", async (IntegrationKind kind, ScenarioBody b, HttpContext h, ActorResolver a, IntegrationHubService s, CancellationToken ct) =>
        {
            await s.SetScenarioAsync(await Me(h, a, ct), kind, b.Mode, ct);
            return Results.NoContent();
        });
        api.MapPost("/integrations/{kind}/reset", async (IntegrationKind kind, HttpContext h, ActorResolver a, IntegrationHubService s, CancellationToken ct) =>
        {
            await s.ResetMockAsync(await Me(h, a, ct), kind, ct);
            return Results.NoContent();
        });
        api.MapGet("/integrations/attempts", async (IntegrationKind? kind, AttemptOutcome? outcome, string? correlationId, int? page, HttpContext h, ActorResolver a, IntegrationHubService s, CancellationToken ct) =>
        {
            var (rows, total) = await s.AttemptsAsync(await Me(h, a, ct), kind, outcome, correlationId, page ?? 1, ct);
            return Results.Ok(new { rows, total });
        });
        api.MapGet("/integrations/attempts/{id:guid}", async (Guid id, HttpContext h, ActorResolver a, IntegrationHubService s, CancellationToken ct) => Results.Ok(await s.AttemptAsync(await Me(h, a, ct), id, ct)));
        api.MapGet("/weather", async (Guid venueId, DateOnly date, bool? refresh, HttpContext h, ActorResolver a, WeatherQueryService s, CancellationToken ct) =>
            Results.Ok(await s.ForecastAsync(await Me(h, a, ct), venueId, date, refresh ?? false, ct)));
        api.MapPost("/venues/{id:guid}/geocode", async (Guid id, HttpContext h, ActorResolver a, GeocodingService s, CancellationToken ct) => Results.Ok(await s.LookupAsync(await Me(h, a, ct), id, ct)));
        api.MapPost("/venues/{id:guid}/geocode/confirm", async (Guid id, ConfirmGeocodeBody b, HttpContext h, ActorResolver a, GeocodingService s, CancellationToken ct) =>
        {
            await s.ConfirmAsync(await Me(h, a, ct), b.CandidateId, b.Version, ct);
            return Results.NoContent();
        });
        api.MapGet("/imports", async (int? page, HttpContext h, ActorResolver a, PartnerImportService s, CancellationToken ct) =>
        {
            var (rows, total) = await s.BatchesAsync(await Me(h, a, ct), page ?? 1, ct);
            return Results.Ok(new { rows, total });
        });
        api.MapGet("/imports/{id:guid}", async (Guid id, HttpContext h, ActorResolver a, PartnerImportService s, CancellationToken ct) =>
        {
            var (batch, errors) = await s.BatchAsync(await Me(h, a, ct), id, ct);
            return Results.Ok(new { batch, errors });
        });
        api.MapGet("/imports/{id:guid}/errors.csv", async (Guid id, HttpContext h, ActorResolver a, PartnerImportService s, CancellationToken ct) =>
            Results.File(await s.ErrorReportAsync(await Me(h, a, ct), id, ct), "text/csv; charset=utf-8", $"import-errors-{id:N}.csv"));
        api.MapPost("/imports/fetch", async (HttpContext h, ActorResolver a, PartnerImportService s, CancellationToken ct) =>
            Results.Ok(await s.ImportDropFolderAsync(await Me(h, a, ct), "manual", ct)));
        api.MapPost("/imports/{id:guid}/replay", async (Guid id, HttpContext h, ActorResolver a, PartnerImportService s, CancellationToken ct) => Results.Ok(await s.ReplayAsync(await Me(h, a, ct), id, ct)));
        api.MapPost("/imports/upload", async (HttpContext h, ActorResolver a, PartnerImportService s, CancellationToken ct) =>
        {
            var form = await h.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw ValidationException.For("file", "Choose a CSV file.");
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            return Results.Ok(await s.ImportUploadAsync(await Me(h, a, ct), file.FileName, buffer.ToArray(), bool.TryParse(form["replay"], out var r) && r, ct));
        }).DisableAntiforgery().AddEndpointFilter<AntiforgeryFilter>();
        api.MapPost("/imports/mock-drop", async (DropBody b, HttpContext h, ActorResolver a, PartnerImportService s, CancellationToken ct) =>
            Results.Ok(new { key = await s.DropMockFileAsync(await Me(h, a, ct), b.Kind, ct) }));
        api.MapPost("/reconciliation/{id:guid}/resolve", async (Guid id, ReconcileBody b, HttpContext h, ActorResolver a, CalendarSyncService s, CancellationToken ct) =>
        {
            await s.ResolveAsync(await Me(h, a, ct), id, b.Reason, ct);
            return Results.NoContent();
        });

        // ---------------- dashboard and reports (E11)
        api.MapGet("/dashboard", async (HttpContext h, ActorResolver a, DashboardService s, CancellationToken ct) => Results.Ok(await s.GetAsync(await Me(h, a, ct), ct)));
        api.MapGet("/reports", async (DateOnly from, DateOnly to, Guid? cemeteryId, HttpContext h, ActorResolver a, DashboardService s, CancellationToken ct) =>
            Results.Ok(await s.ReportAsync(await Me(h, a, ct), new ReportFilter(from, to, cemeteryId), ct)));
        api.MapGet("/reports/{report}.csv", async (string report, DateOnly from, DateOnly to, Guid? cemeteryId, HttpContext h, ActorResolver a, DashboardService s, CancellationToken ct) =>
            Results.File(await s.ReportCsvAsync(await Me(h, a, ct), new ReportFilter(from, to, cemeteryId), report, ct), "text/csv; charset=utf-8", $"{report}-{from:yyyyMMdd}-{to:yyyyMMdd}.csv"));
    }

    private static VenueInput Venue(VenueBody b) => new(b.CemeteryId, b.Code, b.Name, b.Address, b.Capacity, b.SetupBufferMinutes, b.TeardownBufferMinutes,
        b.OpeningHours ?? [], b.AllowedCategoryCodes ?? [], b.IsActive);
}

public sealed class AuditParams
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public string? Actor { get; init; }
    public string? EntityType { get; init; }
    public string? Action { get; init; }
    public string? CorrelationId { get; init; }
    public string? Reference { get; init; }
    public int? Page { get; init; }

    public AuditFilter ToFilter() => new(From, To, Actor, EntityType, Action, CorrelationId, Reference, Page ?? 1);
}
