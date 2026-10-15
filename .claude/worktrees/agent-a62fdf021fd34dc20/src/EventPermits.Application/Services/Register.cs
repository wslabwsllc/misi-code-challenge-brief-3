using System.Globalization;
using System.Text;
using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public enum RegisterSort { Updated, Reference, EventDate, Status, Applicant }

public sealed record RegisterQuery(
    string? Search = null, CaseStatus[]? Statuses = null, Guid? CemeteryId = null, Guid? VenueId = null, Guid? CategoryId = null, string? Applicant = null,
    DateOnly? From = null, DateOnly? To = null, RegisterSort Sort = RegisterSort.Updated, bool Descending = true, int Page = 1, bool MyDrafts = false);

public sealed record RegisterRow(Guid Id, string Reference, CaseStatus Status, string ApplicantName, string Title, string? Category, string? Cemetery, string? Venue,
    DateTimeOffset? StartUtc, string? TimeZoneId, DateTimeOffset UpdatedAt, string OwnerName, uint Version);

public sealed record RegisterPage(IReadOnlyList<RegisterRow> Rows, int Total, int Page, int PageSize);

public sealed record SavedViewDto(Guid Id, string Name, string Query, uint Version);

/// <summary>Application Register (FR-03, E4-02): scope applied before search, counts, paging and export.</summary>
public sealed partial class RegisterService(AppDb dbs, ICorrelationContext correlation) : ServiceBase(dbs, correlation)
{
    public async Task<RegisterPage> ListAsync(Actor actor, RegisterQuery query, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewRegister);
        if (query.Page < 1 || query.Page > 10_000) throw ValidationException.For("page", "Page must be between 1 and 10000.");
        await using var db = await Dbs.CreateAsync(actor, ct);
        var q = Filter(db, actor, query);
        var total = await q.CountAsync(ct);
        var rows = await Project(db, Sort(q, query)).Skip((query.Page - 1) * Limits.PageSize).Take(Limits.PageSize).ToListAsync(ct);
        // Auditors see applicants masked on screen exactly as in the CSV export (plan §3.2 page 02, A-21).
        if (CaseScope.MasksContacts(actor)) rows = rows.Select(r => r with { ApplicantName = Initials(r.ApplicantName) }).ToList();
        return new RegisterPage(rows, total, query.Page, Limits.PageSize);
    }

    /// <summary>CSV of the current filtered result (all pages), role-permitted columns, masked where required, formula-safe.</summary>
    public async Task<byte[]> ExportCsvAsync(Actor actor, RegisterQuery query, CancellationToken ct = default)
    {
        actor.Require(Cap.ExportRegister);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var rows = await Project(db, Sort(Filter(db, actor, query), query)).Take(10_000).ToListAsync(ct);
        var masked = CaseScope.MasksContacts(actor);
        var sb = new StringBuilder();
        sb.AppendLine("Reference,Status,Applicant,Event,Category,Cemetery,Venue,Event start (UTC),Updated (UTC),Owner");
        foreach (var r in rows)
        {
            sb.AppendJoin(',', new[]
            {
                r.Reference, Workflow.Display(r.Status), masked ? Initials(r.ApplicantName) : r.ApplicantName, r.Title, r.Category ?? "", r.Cemetery ?? "", r.Venue ?? "",
                r.StartUtc?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "",
                r.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), r.OwnerName,
            }.Select(Csv.Cell));
            sb.AppendLine();
        }
        await using (var audit = await Dbs.CreateAsync(actor, ct))
        {
            AuditLog(audit, actor, "Register.Exported", "Register", "csv", $"Register exported ({rows.Count} rows)");
            await audit.SaveChangesAsync(ct);
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public async Task<IReadOnlyList<SavedViewDto>> ViewsAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewRegister);
        await using var db = await Dbs.CreateAsync(actor, ct);
        return await db.SavedViews.AsNoTracking().Where(v => v.UserId == actor.UserId && !v.Deleted).OrderBy(v => v.Name)
            .Select(v => new SavedViewDto(v.Id, v.Name, v.Query, v.Version)).ToListAsync(ct);
    }

    public async Task<SavedViewDto> SaveViewAsync(Actor actor, Guid? id, string? name, string? queryString, uint? version, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewRegister);
        new Validator().Text("name", name, 2, 60, "View name").Text("query", queryString, 0, 1000, "Filters", false).ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        SavedView view;
        if (id is { } existing)
        {
            view = await db.SavedViews.FirstOrDefaultAsync(v => v.Id == existing && v.UserId == actor.UserId && !v.Deleted, ct) ?? throw new NotFoundException();
            ExpectVersion(db, view, version);
        }
        else
        {
            view = new SavedView { UserId = actor.UserId };
            db.SavedViews.Add(view);
        }
        if (await db.SavedViews.AnyAsync(v => v.UserId == actor.UserId && !v.Deleted && v.Name == name!.Trim() && v.Id != view.Id, ct))
            throw ValidationException.For("name", "You already have a view with this name.");
        view.Name = name!.Trim();
        view.Query = (queryString ?? "").TrimStart('?');
        await SaveAsync(db, ct);
        return new SavedViewDto(view.Id, view.Name, view.Query, view.Version);
    }

    public async Task DeleteViewAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewRegister);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var view = await db.SavedViews.FirstOrDefaultAsync(v => v.Id == id && v.UserId == actor.UserId && !v.Deleted, ct) ?? throw new NotFoundException();
        view.Deleted = true; // soft delete: no hard deletion anywhere
        await SaveAsync(db, ct);
    }

    private static IQueryable<PermitApplication> Filter(AppDbContext db, Actor actor, RegisterQuery query)
    {
        var q = Scoped(db, actor).AsNoTracking();
        if (query.MyDrafts) q = q.Where(a => a.OwnerUserId == actor.UserId && a.Status == CaseStatus.Draft);
        if (query.Statuses is { Length: > 0 } statuses) q = q.Where(a => statuses.Contains(a.Status));
        if (query.CemeteryId is { } c) q = q.Where(a => a.Event!.CemeteryId == c);
        if (query.VenueId is { } v) q = q.Where(a => a.Event!.PreferredVenueId == v);
        if (query.CategoryId is { } cat) q = q.Where(a => a.Event!.CategoryId == cat);
        if (!string.IsNullOrWhiteSpace(query.Applicant))
        {
            var term = query.Applicant.Trim().ToLower();
            q = q.Where(a => a.Applicant!.FullName.ToLower().Contains(term) || a.Applicant.Organization.ToLower().Contains(term));
        }
        if (query.From is { } from) q = q.Where(a => a.Event!.StartUtc >= new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        if (query.To is { } to) q = q.Where(a => a.Event!.StartUtc < new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLower();
            q = q.Where(a => a.Reference.ToLower().Contains(term) || a.Applicant!.FullName.ToLower().Contains(term) || a.Event!.Title.ToLower().Contains(term));
        }
        return q;
    }

    private static IQueryable<PermitApplication> Sort(IQueryable<PermitApplication> q, RegisterQuery query) => (query.Sort, query.Descending) switch
    {
        (RegisterSort.Reference, false) => q.OrderBy(a => a.Reference),
        (RegisterSort.Reference, true) => q.OrderByDescending(a => a.Reference),
        (RegisterSort.EventDate, false) => q.OrderBy(a => a.Event!.StartUtc == null).ThenBy(a => a.Event!.StartUtc).ThenBy(a => a.Reference),
        (RegisterSort.EventDate, true) => q.OrderBy(a => a.Event!.StartUtc == null).ThenByDescending(a => a.Event!.StartUtc).ThenBy(a => a.Reference),
        (RegisterSort.Status, false) => q.OrderBy(a => a.Status).ThenBy(a => a.Reference),
        (RegisterSort.Status, true) => q.OrderByDescending(a => a.Status).ThenBy(a => a.Reference),
        (RegisterSort.Applicant, false) => q.OrderBy(a => a.Applicant!.FullName).ThenBy(a => a.Reference),
        (RegisterSort.Applicant, true) => q.OrderByDescending(a => a.Applicant!.FullName).ThenBy(a => a.Reference),
        (_, false) => q.OrderBy(a => a.UpdatedAt).ThenBy(a => a.Reference),
        _ => q.OrderByDescending(a => a.UpdatedAt).ThenBy(a => a.Reference),
    };

    private static IQueryable<RegisterRow> Project(AppDbContext db, IQueryable<PermitApplication> q) => q.Select(a => new RegisterRow(
        a.Id, a.Reference, a.Status, a.Applicant!.FullName, a.Event!.Title, a.Event.Category!.Name, a.Event.Cemetery!.Name, a.Event.PreferredVenue!.Name,
        a.Event.StartUtc, a.Event.Cemetery.TimeZoneId, a.UpdatedAt, db.Users.Where(u => u.Id == a.OwnerUserId).Select(u => u.DisplayName).FirstOrDefault() ?? "", a.Version));

    private static string Initials(string name) => string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => p[0] + "."));
}

/// <summary>RFC 4180 quoting plus spreadsheet formula neutralisation (cells starting with = + - @ tab or CR).</summary>
public static class Csv
{
    public static string Cell(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static string Line(IEnumerable<string?> cells) => string.Join(',', cells.Select(Cell));
}
