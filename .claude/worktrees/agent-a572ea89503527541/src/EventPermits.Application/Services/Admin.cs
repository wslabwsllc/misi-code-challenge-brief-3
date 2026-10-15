using System.Globalization;
using System.Text;
using EventPermits.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public sealed record UserRow(string Id, string UserName, string DisplayName, string Email, bool IsActive, IReadOnlyList<string> Roles, DateTimeOffset? LockoutEnd, string ConcurrencyStamp);
public sealed record NewUserInput(string? UserName, string? DisplayName, string? Email, IReadOnlyList<string> Roles, string? InitialPassword);
public sealed record RuleRow(Guid Id, string Key, string Value, string Description, int? Min, int? Max, uint Version);
public sealed record TemplateRow(Guid Id, string Code, string Name, NotificationChannel Channel, string Subject, string Body, bool IsActive, uint Version);
public sealed record AuditFilter(DateOnly? From = null, DateOnly? To = null, string? Actor = null, string? EntityType = null, string? Action = null, string? CorrelationId = null, string? Reference = null, int Page = 1);
public sealed record AuditRow(long Id, DateTimeOffset At, string ActorName, string Action, string EntityType, string EntityId, string? Reference, string Summary, string? Reason, string CorrelationId);

public sealed record VenueInput(Guid CemeteryId, string? Code, string? Name, string? Address, int Capacity, int SetupBufferMinutes, int TeardownBufferMinutes,
    IReadOnlyList<OpeningHours> OpeningHours, IReadOnlyList<string> AllowedCategoryCodes, bool IsActive);
public sealed record VenueRow(Guid Id, Guid CemeteryId, string CemeteryName, string TimeZoneId, string Code, string Name, string Address, int Capacity, int SetupBufferMinutes,
    int TeardownBufferMinutes, IReadOnlyList<OpeningHours> OpeningHours, IReadOnlyList<string> AllowedCategoryCodes, bool IsActive, double? Latitude, double? Longitude,
    bool CoordinatesVerified, string? GeocodeLabel, double? GeocodeConfidence, uint Version);
public sealed record BlackoutRow(Guid Id, Guid VenueId, string VenueName, string TimeZoneId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason, bool IsActive, IReadOnlyList<string> AffectedReferences, uint Version);
public sealed record BlackoutInput(Guid VenueId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string? Reason);
public sealed record ResourceRow(Guid Id, Guid CemeteryId, string CemeteryName, string Code, string Name, string Unit, int TotalQuantity, bool IsActive, uint Version);
public sealed record CategoryRow(Guid Id, string Code, string Name, string Description, IReadOnlyList<DocumentType> RequiredDocuments, IReadOnlyList<string> ReviewChecklist, bool IsActive, uint Version);
public sealed record CemeteryRow(Guid Id, string Code, string Name, string Address, string TimeZoneId);

/// <summary>Administration: users and roles, business rules, templates, audit viewer (E1-05) and venues, closures, stock, categories (E6-01, FR-14).</summary>
public sealed partial class AdminService(AppDb dbs, ICorrelationContext correlation, UserManager<AppUser> users) : ServiceBase(dbs, correlation)
{
    // ------------------------------------------------------------------ users and roles

    public async Task<IReadOnlyList<UserRow>> UsersAsync(Actor actor, string? search, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageUsers);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var q = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            q = q.Where(u => u.UserName!.ToLower().Contains(term) || u.DisplayName.ToLower().Contains(term));
        }
        var list = await q.OrderBy(u => u.DisplayName).ToListAsync(ct);
        var roles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id select new { ur.UserId, r.Name }).ToListAsync(ct);
        return list.Select(u => new UserRow(u.Id, u.UserName ?? "", u.DisplayName, u.Email ?? "", u.IsActive,
            roles.Where(r => r.UserId == u.Id).Select(r => r.Name!).OrderBy(r => r).ToList(), u.LockoutEnd, u.ConcurrencyStamp ?? "")).ToList();
    }

    public async Task<string> CreateUserAsync(Actor actor, NewUserInput input, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageUsers);
        var v = new Validator()
            .Text("userName", input.UserName, 3, 40, "User name")
            .Text("displayName", input.DisplayName, 3, 120, "Display name")
            .Email("email", input.Email, "Email")
            .Require(input.Roles.Count > 0, "roles", "Choose at least one role.")
            .Require(input.Roles.All(Roles.All.Contains), "roles", "Unknown role.")
            .Text("initialPassword", input.InitialPassword, 12, 128, "Initial password");
        if (input.UserName is { } un && !System.Text.RegularExpressions.Regex.IsMatch(un.Trim(), "^[a-z0-9._-]+$"))
            v.Add("userName", "Use lower-case letters, digits, dots, dashes or underscores.");
        v.ThrowIfInvalid();
        if (await users.FindByNameAsync(input.UserName!.Trim()) is not null) throw ValidationException.For("userName", "This user name is taken.");
        var user = new AppUser { UserName = input.UserName.Trim(), DisplayName = input.DisplayName!.Trim(), Email = input.Email!.Trim(), EmailConfirmed = true, IsActive = true, LockoutEnabled = true, CreatedAt = Now };
        var created = await users.CreateAsync(user, input.InitialPassword!);
        if (!created.Succeeded) throw new ValidationException(new Dictionary<string, string[]> { ["initialPassword"] = created.Errors.Select(e => e.Description).ToArray() });
        await users.AddToRolesAsync(user, input.Roles);
        await using var db = await Dbs.CreateAsync(actor, ct);
        AuditLog(db, actor, "User.Created", nameof(AppUser), user.Id, $"{user.UserName} created with roles {string.Join(", ", input.Roles)}");
        await db.SaveChangesAsync(ct);
        return user.Id;
    }

    /// <summary>Deactivation (no deletion). Updating the security stamp ends existing sessions within a minute; commands fail at once.</summary>
    public async Task SetActiveAsync(Actor actor, string userId, bool active, string? reason, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageUsers);
        new Validator().Text("reason", reason, Limits.ReasonMin, Limits.ReasonMax, "Reason").ThrowIfInvalid();
        if (userId == actor.UserId && !active) throw new BusinessRuleException(ErrorCodes.SelfLockout, "You cannot deactivate your own account.");
        var user = await users.FindByIdAsync(userId) ?? throw new NotFoundException();
        if (user.IsActive == active) return;
        user.IsActive = active;
        await users.UpdateAsync(user);
        await users.UpdateSecurityStampAsync(user);
        await using var db = await Dbs.CreateAsync(actor, ct);
        AuditLog(db, actor, active ? "User.Activated" : "User.Deactivated", nameof(AppUser), user.Id, $"{user.UserName} {(active ? "activated" : "deactivated")}", reason: reason!.Trim());
        await db.SaveChangesAsync(ct);
    }

    public async Task SetRoleAsync(Actor actor, string userId, string role, bool grant, string? reason, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageUsers);
        new Validator().Require(Roles.All.Contains(role), "role", "Unknown role.").Text("reason", reason, Limits.ReasonMin, Limits.ReasonMax, "Reason").ThrowIfInvalid();
        if (userId == actor.UserId && role == Roles.SystemAdmin && !grant)
            throw new BusinessRuleException(ErrorCodes.SelfLockout, "You cannot remove your own System Admin role.");
        var user = await users.FindByIdAsync(userId) ?? throw new NotFoundException();
        var has = await users.IsInRoleAsync(user, role);
        if (has == grant) return;
        var result = grant ? await users.AddToRoleAsync(user, role) : await users.RemoveFromRoleAsync(user, role);
        if (!result.Succeeded) throw new BusinessRuleException("ROLE_CHANGE_FAILED", string.Join(" ", result.Errors.Select(e => e.Description)));
        await users.UpdateSecurityStampAsync(user);
        await using var db = await Dbs.CreateAsync(actor, ct);
        AuditLog(db, actor, grant ? "Role.Granted" : "Role.Removed", nameof(AppUser), user.Id, $"{Roles.Display(role)} {(grant ? "granted to" : "removed from")} {user.UserName}", reason: reason!.Trim());
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ rules and templates

    public async Task<IReadOnlyList<RuleRow>> RulesAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewAudit);
        await using var db = await Dbs.CreateAsync(actor, ct);
        return await db.RuleSettings.AsNoTracking().OrderBy(r => r.Key).Select(r => new RuleRow(r.Id, r.Key, r.Value, r.Description, r.Min, r.Max, r.Version)).ToListAsync(ct);
    }

    public async Task UpdateRuleAsync(Actor actor, Guid id, uint? version, string? value, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageRules);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var rule = await db.RuleSettings.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException();
        if (!int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            throw ValidationException.For("value", "Enter a whole number.");
        if (rule.Min is { } min && number < min || rule.Max is { } max && number > max)
            throw ValidationException.For("value", $"The value must be between {rule.Min} and {rule.Max}.");
        ExpectVersion(db, rule, version);
        var before = rule.Value;
        rule.Value = number.ToString(CultureInfo.InvariantCulture);
        AuditLog(db, actor, "Rule.Changed", nameof(RuleSetting), rule.Key, $"{rule.Key}: {before} → {rule.Value}", before: before, after: rule.Value);
        await SaveAsync(db, ct);
        RuleService.Invalidate();
    }

    public async Task<IReadOnlyList<TemplateRow>> TemplatesAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewNotifications);
        await using var db = await Dbs.CreateAsync(actor, ct);
        return await db.NotificationTemplates.AsNoTracking().OrderBy(t => t.Code)
            .Select(t => new TemplateRow(t.Id, t.Code, t.Name, t.Channel, t.Subject, t.Body, t.IsActive, t.Version)).ToListAsync(ct);
    }

    /// <summary>Template edits are validated: placeholders must be known (E1-05 AC3).</summary>
    public async Task UpdateTemplateAsync(Actor actor, Guid id, uint? version, string? subject, string? body, bool isActive, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageRules);
        var v = new Validator().Text("subject", subject, 3, 200, "Subject").Text("body", body, 10, 4000, "Body");
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches((subject ?? "") + (body ?? ""), @"\{\{(\w+)\}\}"))
            if (!NotificationOutbox.Placeholders.Contains(m.Groups[1].Value)) v.Add("body", $"Unknown placeholder {{{{{m.Groups[1].Value}}}}}. Allowed: {string.Join(", ", NotificationOutbox.Placeholders)}.");
        v.ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var t = await db.NotificationTemplates.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        ExpectVersion(db, t, version);
        var before = new { t.Subject, t.IsActive };
        t.Subject = subject!.Trim();
        t.Body = body!.Trim();
        t.IsActive = isActive;
        AuditLog(db, actor, "Template.Changed", nameof(NotificationTemplate), t.Code, $"Template {t.Code} updated", before: before, after: new { t.Subject, t.IsActive });
        await SaveAsync(db, ct);
    }

    public static string Preview(string text) => NotificationOutbox.Fill(text, new Dictionary<string, string>
    {
        ["ApplicantName"] = "Avery Practice-Alder", ["Reference"] = "APP-2026-00042", ["EventTitle"] = "Remembrance service", ["EventDate"] = "Tuesday 17 November 2026, 10:00",
        ["Venue"] = "Riverside Chapel of Light", ["PermitNumber"] = "PER-2026-00007", ["Reason"] = "Example reason", ["Items"] = "Signed event plan", ["DueDate"] = "Friday 30 October 2026",
        ["Message"] = "Example message text.",
    });

    // ------------------------------------------------------------------ audit viewer

    public async Task<(IReadOnlyList<AuditRow> Rows, int Total)> AuditAsync(Actor actor, AuditFilter filter, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewAudit);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var q = AuditQuery(db, filter);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.e.At).ThenByDescending(x => x.e.Id).Skip((Math.Max(1, filter.Page) - 1) * Limits.PageSize).Take(Limits.PageSize).ToListAsync(ct);
        return (rows.Select(x => new AuditRow(x.e.Id, x.e.At, x.e.ActorName, x.e.Action, x.e.EntityType, x.e.EntityId, x.Reference, x.e.Summary, x.e.Reason, x.e.CorrelationId)).ToList(), total);
    }

    /// <summary>Audit CSV for the Compliance Auditor (brief §3).</summary>
    public async Task<byte[]> AuditCsvAsync(Actor actor, AuditFilter filter, CancellationToken ct = default)
    {
        actor.Require(Cap.ExportAudit);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var rows = await AuditQuery(db, filter).OrderByDescending(x => x.e.At).Take(20_000).ToListAsync(ct);
        var sb = new StringBuilder();
        sb.AppendLine("At (UTC),Actor,Action,Entity type,Entity id,Case,Summary,Reason,Correlation id");
        foreach (var x in rows)
            sb.AppendLine(Csv.Line([x.e.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), x.e.ActorName, x.e.Action, x.e.EntityType, x.e.EntityId, x.Reference, x.e.Summary, x.e.Reason, x.e.CorrelationId]));
        AuditLog(db, actor, "Audit.Exported", "Audit", "csv", $"Audit CSV exported ({rows.Count} rows)");
        await db.SaveChangesAsync(ct);
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static IQueryable<AuditJoin> AuditQuery(AppDbContext db, AuditFilter f)
    {
        var q = from e in db.AuditEvents.AsNoTracking()
                join a in db.Applications.AsNoTracking() on e.ApplicationId equals a.Id into apps
                from a in apps.DefaultIfEmpty()
                select new AuditJoin { e = e, Reference = a == null ? null : a.Reference };
        if (f.From is { } from) q = q.Where(x => x.e.At >= new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        if (f.To is { } to) q = q.Where(x => x.e.At < new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        if (!string.IsNullOrWhiteSpace(f.Actor)) q = q.Where(x => x.e.ActorName.ToLower().Contains(f.Actor.Trim().ToLower()));
        if (!string.IsNullOrWhiteSpace(f.EntityType)) q = q.Where(x => x.e.EntityType == f.EntityType);
        if (!string.IsNullOrWhiteSpace(f.Action)) q = q.Where(x => x.e.Action.StartsWith(f.Action.Trim()));
        if (!string.IsNullOrWhiteSpace(f.CorrelationId)) q = q.Where(x => x.e.CorrelationId == f.CorrelationId.Trim());
        if (!string.IsNullOrWhiteSpace(f.Reference)) q = q.Where(x => x.Reference != null && x.Reference.Contains(f.Reference.Trim().ToUpper()));
        return q;
    }

    private sealed class AuditJoin
    {
        public AuditEvent e { get; init; } = default!;
        public string? Reference { get; init; }
    }

    // ------------------------------------------------------------------ venues, closures, stock, categories

    public async Task<IReadOnlyList<CemeteryRow>> CemeteriesAsync(Actor actor, CancellationToken ct = default)
    {
        await using var db = await Dbs.CreateAsync(actor, ct);
        return await db.Cemeteries.AsNoTracking().OrderBy(c => c.Name).Select(c => new CemeteryRow(c.Id, c.Code, c.Name, c.Address, c.TimeZoneId)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<VenueRow>> VenuesAsync(Actor actor, CancellationToken ct = default)
    {
        if (!actor.Can(Cap.ViewVenues) && !actor.Can(Cap.ViewMap) && !actor.Can(Cap.ViewSchedule)) throw new ForbiddenException();
        await using var db = await Dbs.CreateAsync(actor, ct);
        return (await db.Venues.AsNoTracking().Include(v => v.Cemetery).OrderBy(v => v.Cemetery!.Name).ThenBy(v => v.Name).ToListAsync(ct)).Select(Row).ToList();
    }

    public async Task<VenueRow> SaveVenueAsync(Actor actor, Guid? id, uint? version, VenueInput input, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageVenues);
        var v = new Validator()
            .Text("code", input.Code, 3, 20, "Code").Text("name", input.Name, 3, 120, "Name").Text("address", input.Address, 5, 200, "Address")
            .Require(input.Capacity is >= 1 and <= 5000, "capacity", "Capacity must be 1 to 5000.")
            .Require(input.SetupBufferMinutes is >= 0 and <= 240, "setupBufferMinutes", "Setup buffer must be 0 to 240 minutes.")
            .Require(input.TeardownBufferMinutes is >= 0 and <= 240, "teardownBufferMinutes", "Teardown buffer must be 0 to 240 minutes.")
            .Require(input.OpeningHours.All(h => h.Opens < h.Closes), "openingHours", "Each day must close after it opens.")
            .Require(input.OpeningHours.Select(h => h.Day).Distinct().Count() == input.OpeningHours.Count, "openingHours", "Give each weekday only once.");
        if (input.Code is { } code && !System.Text.RegularExpressions.Regex.IsMatch(code.Trim(), "^[A-Z0-9-]+$")) v.Add("code", "Use upper-case letters, digits and dashes.");
        v.ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        if (!await db.Cemeteries.AnyAsync(c => c.Id == input.CemeteryId, ct)) throw ValidationException.For("cemeteryId", "Choose a cemetery.");
        if (await db.Venues.AnyAsync(x => x.Code == input.Code!.Trim() && x.Id != id, ct)) throw ValidationException.For("code", "This code is used by another venue.");
        Venue venue;
        if (id is { } existing)
        {
            venue = await db.Venues.FirstOrDefaultAsync(x => x.Id == existing, ct) ?? throw new NotFoundException();
            ExpectVersion(db, venue, version);
        }
        else
        {
            venue = new Venue();
            db.Venues.Add(venue);
        }
        var before = id is null ? null : new { venue.Name, venue.Capacity, venue.SetupBufferMinutes, venue.TeardownBufferMinutes, venue.IsActive, venue.Address };
        var addressChanged = venue.Address != input.Address!.Trim();
        venue.CemeteryId = input.CemeteryId;
        venue.Code = input.Code!.Trim();
        venue.Name = input.Name!.Trim();
        venue.Address = input.Address.Trim();
        venue.Capacity = input.Capacity;
        venue.SetupBufferMinutes = input.SetupBufferMinutes;
        venue.TeardownBufferMinutes = input.TeardownBufferMinutes;
        venue.OpeningHours = input.OpeningHours.OrderBy(h => h.Day).ToList();
        venue.AllowedCategoryCodes = input.AllowedCategoryCodes.ToList();
        venue.IsActive = input.IsActive;
        if (addressChanged && id is not null)
        {
            // A new address invalidates the confirmed coordinates (no invented GPS, E6-08).
            venue.CoordinatesVerified = false;
        }
        AuditLog(db, actor, id is null ? "Venue.Created" : "Venue.Changed", nameof(Venue), venue.Id, $"Venue {venue.Code} {(id is null ? "created" : "updated")}", before: before,
            after: new { venue.Name, venue.Capacity, venue.SetupBufferMinutes, venue.TeardownBufferMinutes, venue.IsActive, venue.Address });
        await SaveAsync(db, ct);
        return Row(await db.Venues.AsNoTracking().Include(x => x.Cemetery).FirstAsync(x => x.Id == venue.Id, ct));
    }

    public async Task<IReadOnlyList<BlackoutRow>> BlackoutsAsync(Actor actor, Guid? venueId, CancellationToken ct = default)
    {
        if (!actor.Can(Cap.ViewVenues) && !actor.Can(Cap.ViewSchedule)) throw new ForbiddenException();
        await using var db = await Dbs.CreateAsync(actor, ct);
        var rows = await db.VenueBlackouts.AsNoTracking().Include(b => b.Venue!).ThenInclude(v => v!.Cemetery)
            .Where(b => venueId == null || b.VenueId == venueId).OrderByDescending(b => b.IsActive).ThenBy(b => b.StartUtc).ToListAsync(ct);
        var result = new List<BlackoutRow>();
        foreach (var b in rows)
        {
            var affected = await db.Reservations.AsNoTracking().Where(r => r.VenueId == b.VenueId && r.Status != ReservationStatus.Released && r.BlockStartUtc < b.EndUtc && r.BlockEndUtc > b.StartUtc)
                .Join(db.Applications, r => r.ApplicationId, a => a.Id, (r, a) => a.Reference).ToListAsync(ct);
            result.Add(new BlackoutRow(b.Id, b.VenueId, b.Venue!.Name, b.Venue.Cemetery!.TimeZoneId, b.StartUtc, b.EndUtc, b.Reason, b.IsActive, affected, b.Version));
        }
        return result;
    }

    /// <summary>Hard closure; existing reservations that overlap are listed as warnings (E6-01 AC2).</summary>
    public async Task<BlackoutRow> AddBlackoutAsync(Actor actor, BlackoutInput input, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageVenues);
        new Validator().Require(input.EndUtc > input.StartUtc, "endUtc", "The closure must end after it starts.").Text("reason", input.Reason, 3, 300, "Reason").ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        if (!await db.Venues.AnyAsync(v => v.Id == input.VenueId, ct)) throw ValidationException.For("venueId", "Choose a venue.");
        var blackout = new VenueBlackout { VenueId = input.VenueId, StartUtc = input.StartUtc, EndUtc = input.EndUtc, Reason = input.Reason!.Trim() };
        db.VenueBlackouts.Add(blackout);
        AuditLog(db, actor, "Blackout.Created", nameof(VenueBlackout), blackout.Id, $"Closure {input.StartUtc:yyyy-MM-dd HH:mm}–{input.EndUtc:yyyy-MM-dd HH:mm} UTC: {blackout.Reason}");
        await SaveAsync(db, ct);
        return (await BlackoutsAsync(actor, input.VenueId, ct)).First(b => b.Id == blackout.Id);
    }

    public async Task SetBlackoutActiveAsync(Actor actor, Guid id, uint? version, bool active, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageVenues);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var b = await db.VenueBlackouts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException();
        ExpectVersion(db, b, version);
        b.IsActive = active;
        AuditLog(db, actor, active ? "Blackout.Reactivated" : "Blackout.Deactivated", nameof(VenueBlackout), b.Id, $"Closure {(active ? "reactivated" : "lifted")}: {b.Reason}");
        await SaveAsync(db, ct);
    }

    public async Task<IReadOnlyList<ResourceRow>> ResourcesAsync(Actor actor, CancellationToken ct = default)
    {
        if (!actor.Can(Cap.ViewVenues) && !actor.Can(Cap.ViewSchedule)) throw new ForbiddenException();
        await using var db = await Dbs.CreateAsync(actor, ct);
        return await db.Resources.AsNoTracking().Include(r => r.Cemetery).OrderBy(r => r.Cemetery!.Name).ThenBy(r => r.Name)
            .Select(r => new ResourceRow(r.Id, r.CemeteryId, r.Cemetery!.Name, r.Code, r.Name, r.Unit, r.TotalQuantity, r.IsActive, r.Version)).ToListAsync(ct);
    }

    public async Task SaveResourceAsync(Actor actor, Guid? id, uint? version, Guid cemeteryId, string? code, string? name, string? unit, int total, bool active, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageVenues);
        new Validator().Text("code", code, 2, 20, "Code").Text("name", name, 2, 80, "Name").Text("unit", unit, 1, 20, "Unit")
            .Require(total is >= 0 and <= 10_000, "totalQuantity", "Quantity must be 0 to 10000.").ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        Resource r;
        if (id is { } existing)
        {
            r = await db.Resources.FirstOrDefaultAsync(x => x.Id == existing, ct) ?? throw new NotFoundException();
            ExpectVersion(db, r, version);
        }
        else
        {
            if (await db.Resources.AnyAsync(x => x.CemeteryId == cemeteryId && x.Code == code!.Trim().ToUpper(), ct)) throw ValidationException.For("code", "This code exists at the cemetery.");
            r = new Resource { CemeteryId = cemeteryId, Code = code!.Trim().ToUpperInvariant() };
            db.Resources.Add(r);
        }
        var before = new { r.Name, r.TotalQuantity, r.IsActive };
        r.Name = name!.Trim();
        r.Unit = unit!.Trim();
        r.TotalQuantity = total;
        r.IsActive = active;
        AuditLog(db, actor, id is null ? "Resource.Created" : "Resource.Changed", nameof(Resource), r.Id, $"{r.Name}: stock {total}", before: before, after: new { r.Name, r.TotalQuantity, r.IsActive });
        await SaveAsync(db, ct);
    }

    public async Task<IReadOnlyList<CategoryRow>> CategoriesAsync(Actor actor, CancellationToken ct = default)
    {
        await using var db = await Dbs.CreateAsync(actor, ct);
        return await db.EventCategories.AsNoTracking().OrderBy(c => c.Name)
            .Select(c => new CategoryRow(c.Id, c.Code, c.Name, c.Description, c.RequiredDocuments, c.ReviewChecklist, c.IsActive, c.Version)).ToListAsync(ct);
    }

    public async Task SaveCategoryAsync(Actor actor, Guid? id, uint? version, string? code, string? name, string? description, IReadOnlyList<DocumentType> required,
        IReadOnlyList<string> checklist, bool active, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageVenues);
        var items = checklist.Select(i => i.Trim()).Where(i => i.Length > 0).Distinct().ToList();
        new Validator().Text("code", code, 3, 20, "Code").Text("name", name, 3, 80, "Name").Text("description", description, 0, 400, "Description", false)
            .Require(items.Count > 0, "checklist", "Give at least one review checklist item.").ThrowIfInvalid();
        await using var db = await Dbs.CreateAsync(actor, ct);
        EventCategory c;
        if (id is { } existing)
        {
            c = await db.EventCategories.FirstOrDefaultAsync(x => x.Id == existing, ct) ?? throw new NotFoundException();
            ExpectVersion(db, c, version);
        }
        else
        {
            if (await db.EventCategories.AnyAsync(x => x.Code == code!.Trim().ToUpper(), ct)) throw ValidationException.For("code", "This code exists.");
            c = new EventCategory { Code = code!.Trim().ToUpperInvariant() };
            db.EventCategories.Add(c);
        }
        c.Name = name!.Trim();
        c.Description = description?.Trim() ?? "";
        c.RequiredDocuments = required.Distinct().ToList();
        c.ReviewChecklist = items;
        c.IsActive = active;
        AuditLog(db, actor, id is null ? "Category.Created" : "Category.Changed", nameof(EventCategory), c.Code, $"Category {c.Code} saved");
        await SaveAsync(db, ct);
    }

    public static VenueRow Row(Venue v) => new(v.Id, v.CemeteryId, v.Cemetery?.Name ?? "", v.Cemetery?.TimeZoneId ?? "UTC", v.Code, v.Name, v.Address, v.Capacity, v.SetupBufferMinutes,
        v.TeardownBufferMinutes, v.OpeningHours, v.AllowedCategoryCodes, v.IsActive, v.Latitude, v.Longitude, v.CoordinatesVerified, v.GeocodeLabel, v.GeocodeConfidence, v.Version);
}
