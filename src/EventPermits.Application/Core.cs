using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Text.Json;
using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application;

/// <summary>Appends audit events to the same context, so they commit with the change they describe (FR-18).</summary>
public static class Audit
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static void Write(AppDbContext db, Actor actor, ICorrelationContext correlation, string action, string entityType, object entityId,
        string summary, Guid? applicationId = null, object? before = null, object? after = null, string? reason = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            At = db.Clock.GetUtcNow(),
            ActorUserId = actor.UserId,
            ActorName = actor.DisplayName,
            Action = action,
            EntityType = entityType,
            EntityId = entityId.ToString() ?? "",
            ApplicationId = applicationId,
            Summary = summary.Length > 500 ? summary[..500] : summary,
            Before = before is null ? null : before as string ?? JsonSerializer.Serialize(before, Json),
            After = after is null ? null : after as string ?? JsonSerializer.Serialize(after, Json),
            Reason = reason,
            CorrelationId = correlation.CorrelationId,
        });
    }
}

/// <summary>APP/PER/INC reference numbers from a per-year counter row taken under a row lock (A-07, E2-02).</summary>
public static class ReferenceGenerator
{
    public static async Task<string> NextAsync(AppDbContext db, string prefix, int year, CancellationToken ct)
    {
        // Atomic upsert-and-increment: concurrent callers serialise on the counter row; never count-plus-one.
        var next = await db.Database.SqlQuery<int>($"""
            INSERT INTO "ReferenceCounters" ("Prefix", "Year", "Next") VALUES ({prefix}, {year}, 2)
            ON CONFLICT ("Prefix", "Year") DO UPDATE SET "Next" = "ReferenceCounters"."Next" + 1
            RETURNING "Next" - 1 AS "Value"
            """).ToListAsync(ct);
        var sequence = next.Single();
        if (sequence > Limits.MaxSequence)
            throw new BusinessRuleException(ErrorCodes.NumberRangeExhausted, $"The {prefix} reference range for {year} is exhausted ({Limits.MaxSequence} used).");
        return References.Format(prefix, year, sequence);
    }
}

/// <summary>Business rule settings (page 18) with a short in-process cache invalidated on change (E2-06).</summary>
public sealed class RuleService(IDbContextFactory<AppDbContext> factory)
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();
    private static DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public async Task<int> IntAsync(string key, CancellationToken ct = default)
    {
        var value = await GetAsync(key, ct);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : int.Parse(RuleKeys.Defaults.First(d => d.Key == key).Value, CultureInfo.InvariantCulture);
    }

    public async Task<bool> FlagAsync(string key, CancellationToken ct = default) => await IntAsync(key, ct) == 1;

    public async Task<string> GetAsync(string key, CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow - _loadedAt > TimeSpan.FromSeconds(30)) await ReloadAsync(ct);
        return Cache.TryGetValue(key, out var v) ? v : RuleKeys.Defaults.FirstOrDefault(d => d.Key == key).Value ?? "";
    }

    public static void Invalidate() => _loadedAt = DateTimeOffset.MinValue;

    private async Task ReloadAsync(CancellationToken ct)
    {
        await Lock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _loadedAt <= TimeSpan.FromSeconds(30)) return;
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await db.RuleSettings.AsNoTracking().ToListAsync(ct);
            Cache.Clear();
            foreach (var r in rows) Cache[r.Key] = r.Value;
            _loadedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            Lock.Release();
        }
    }
}

/// <summary>
/// Shared plumbing for application services: contexts attributed to the actor, scope-checked loading,
/// version checks, transitions with audit, and mapping of database concurrency failures to 409.
/// </summary>
public abstract class ServiceBase(AppDb dbs, ICorrelationContext correlation)
{
    protected AppDb Dbs => dbs;
    protected ICorrelationContext Correlation => correlation;
    protected TimeProvider Clock => dbs.Clock;
    protected DateTimeOffset Now => dbs.Clock.GetUtcNow();

    protected void AuditLog(AppDbContext db, Actor actor, string action, string entityType, object entityId, string summary,
        Guid? applicationId = null, object? before = null, object? after = null, string? reason = null) =>
        Audit.Write(db, actor, correlation, action, entityType, entityId, summary, applicationId, before, after, reason);

    /// <summary>Applies the caller's read scope (E1-03): returns null when the case does not exist or is outside scope.</summary>
    protected static IQueryable<PermitApplication> Scoped(AppDbContext db, Actor actor) => CaseScope.Apply(db, db.Applications, actor);

    /// <summary>Loads a case for a command: 404 when missing/out of scope, otherwise tracked.</summary>
    protected static async Task<PermitApplication> LoadCaseAsync(AppDbContext db, Actor actor, Guid id, CancellationToken ct,
        Func<IQueryable<PermitApplication>, IQueryable<PermitApplication>>? include = null)
    {
        var query = Scoped(db, actor);
        if (include is not null) query = include(query);
        return await query.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException();
    }

    /// <summary>Makes the update predicate compare the caller's version (409 when someone saved in between).</summary>
    protected static void ExpectVersion(AppDbContext db, Entity entity, uint? expected, Func<object?>? currentView = null)
    {
        if (expected is null) throw ValidationException.For("version", "The record version is required. Reload the page and try again.");
        if (entity.Version != expected.Value) throw new ConflictException(currentView?.Invoke(), entity.Version);
        db.Entry(entity).Property(e => e.Version).OriginalValue = expected.Value;
    }

    /// <summary>Marks the aggregate as modified so a child-only change still bumps its version (E2-05 AC2).</summary>
    protected void Touch(AppDbContext db, Entity entity)
    {
        entity.UpdatedAt = Now;
        db.Entry(entity).Property(e => e.UpdatedAt).IsModified = true;
    }

    protected static void RequireTransition(PermitApplication app, Transition transition)
    {
        if (!Workflow.IsAllowed(transition, app.Status))
        {
            var rule = Workflow.Rule(transition);
            throw new BusinessRuleException(ErrorCodes.InvalidTransition,
                $"\"{rule.Name}\" ({rule.Id}) is not allowed from {Workflow.Display(app.Status)}. It requires: {string.Join(" or ", rule.From.Select(Workflow.Display))}.");
        }
    }

    /// <summary>Changes status and records the audit event in the same unit of work.</summary>
    protected void ApplyTransition(AppDbContext db, Actor actor, PermitApplication app, Transition transition, string? reason = null)
    {
        RequireTransition(app, transition);
        var rule = Workflow.Rule(transition);
        var from = app.Status;
        app.Status = rule.To;
        app.StatusChangedAt = Now;
        AuditLog(db, actor, $"Transition.{transition}", nameof(PermitApplication), app.Id,
            $"{rule.Id} {rule.Name}: {Workflow.Display(from)} → {Workflow.Display(rule.To)}", app.Id,
            new { status = from.ToString() }, new { status = rule.To.ToString() }, reason);
    }

    /// <summary>Saves and maps a lost concurrency race to 409 with the current version.</summary>
    protected static async Task SaveAsync(AppDbContext db, CancellationToken ct, Func<Task<object?>>? currentView = null)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var entry = ex.Entries.FirstOrDefault();
            uint current = 0;
            if (entry is not null)
            {
                var values = await entry.GetDatabaseValuesAsync(ct);
                if (values is null) throw new NotFoundException();
                current = values.GetValue<uint>(nameof(Entity.Version));
            }
            throw new ConflictException(currentView is null ? null : await currentView(), current);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.GetType().Name == "PostgresException" && ex.InnerException.Message.Contains("23505"))
        {
            throw new ConflictException(null, 0, "The same change was submitted twice or a duplicate value already exists. Reload and check the current state.");
        }
    }

    /// <summary>Serialisable transaction helper for rare multi-statement invariants (numbering, inventory).</summary>
    protected static async Task<T> InTransactionAsync<T>(AppDbContext db, Func<Task<T>> work, CancellationToken ct, IsolationLevel level = IsolationLevel.ReadCommitted)
    {
        await using var tx = await db.Database.BeginTransactionAsync(level, ct);
        var result = await work();
        await tx.CommitAsync(ct);
        return result;
    }
}

/// <summary>Record-level read scope applied before every list, count, export and lookup (E1-03).</summary>
public static class CaseScope
{
    public static IQueryable<PermitApplication> Apply(AppDbContext db, IQueryable<PermitApplication> source, Actor actor)
    {
        // Staff with an all-cases view first; narrower roles widen the set only by their own rows.
        if (actor.Has(Roles.EligibilityReviewer) || actor.Has(Roles.SchedulingOfficer) || actor.Has(Roles.OperationsManager) || actor.Has(Roles.ComplianceAuditor))
            return source;
        var own = actor.Has(Roles.IntakeCoordinator);
        var attendant = actor.Has(Roles.EventAttendant);
        if (!own && !attendant) return source.Where(_ => false);
        var userId = actor.UserId;
        return source.Where(a =>
            (own && a.OwnerUserId == userId) ||
            (attendant && db.AttendantAssignments.Any(x => x.ApplicationId == a.Id && x.UserId == userId && x.Active)));
    }

    /// <summary>Auditors see contact details masked (A-21); attendants see only the on-site contact (A-22).</summary>
    public static bool MasksContacts(Actor actor) =>
        actor.Has(Roles.ComplianceAuditor) && !(actor.Has(Roles.EligibilityReviewer) || actor.Has(Roles.SchedulingOfficer) || actor.Has(Roles.OperationsManager) || actor.Has(Roles.IntakeCoordinator));

    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        return at <= 1 ? "•••" : email[0] + new string('•', Math.Min(6, at - 1)) + email[at..];
    }

    public static string MaskPhone(string phone) => phone.Length <= 4 ? "•••" : new string('•', phone.Length - 4) + phone[^4..];
}
