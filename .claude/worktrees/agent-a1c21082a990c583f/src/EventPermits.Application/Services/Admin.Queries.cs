using EventPermits.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application.Services;

public sealed record AuditFacets(IReadOnlyList<string> EntityTypes, IReadOnlyList<string> ActionGroups);

/// <summary>Page 18 additions: account lock/unlock, validated audit search and the audit filter choices (E1-05).</summary>
public sealed partial class AdminService
{
    /// <summary>
    /// Locks (no end date) or unlocks an account. Unlocking also clears a sign-in lockout after failed attempts.
    /// Updating the security stamp ends the locked user's sessions; the account itself is never deleted.
    /// </summary>
    public async Task SetLockAsync(Actor actor, string userId, bool locked, string? reason, CancellationToken ct = default)
    {
        actor.Require(Cap.ManageUsers);
        new Validator().Text("reason", reason, Limits.ReasonMin, Limits.ReasonMax, "Reason").ThrowIfInvalid();
        if (userId == actor.UserId && locked) throw new BusinessRuleException(ErrorCodes.SelfLockout, "You cannot lock your own account.");
        var user = await users.FindByIdAsync(userId) ?? throw new NotFoundException();
        var isLocked = user.LockoutEnd is { } end && end > Now;
        if (isLocked == locked) return;
        user.LockoutEnabled = true;
        user.LockoutEnd = locked ? DateTimeOffset.MaxValue : null;
        if (!locked) user.AccessFailedCount = 0;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded) throw new BusinessRuleException("LOCK_CHANGE_FAILED", string.Join(" ", result.Errors.Select(e => e.Description)));
        await users.UpdateSecurityStampAsync(user);
        await using var db = await Dbs.CreateAsync(actor, ct);
        AuditLog(db, actor, locked ? "User.Locked" : "User.Unlocked", nameof(AppUser), user.Id, $"{user.UserName} {(locked ? "locked" : "unlocked")}", reason: reason!.Trim());
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Audit viewer search with filter validation (dates in order, bounded text filters).</summary>
    public async Task<(IReadOnlyList<AuditRow> Rows, int Total)> SearchAuditAsync(Actor actor, AuditFilter filter, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewAudit);
        ValidateAuditFilter(filter);
        return await AuditAsync(actor, filter, ct);
    }

    public static void ValidateAuditFilter(AuditFilter filter) => new Validator()
        .Require(filter.From is null || filter.To is null || filter.To >= filter.From, "to", "The end date must not be before the start date.")
        .Text("actor", filter.Actor, 1, 120, "Actor", required: false)
        .Text("action", filter.Action, 1, 80, "Action", required: false)
        .Text("correlationId", filter.CorrelationId, 1, 64, "Correlation ID", required: false)
        .Text("reference", filter.Reference, 1, 20, "Case reference", required: false)
        .ThrowIfInvalid();

    /// <summary>Entity types and action groups present in the log, for the filter drop-downs.</summary>
    public async Task<AuditFacets> AuditFacetsAsync(Actor actor, CancellationToken ct = default)
    {
        actor.Require(Cap.ViewAudit);
        await using var db = await Dbs.CreateAsync(actor, ct);
        var types = await db.AuditEvents.AsNoTracking().Select(e => e.EntityType).Distinct().OrderBy(t => t).ToListAsync(ct);
        var actions = await db.AuditEvents.AsNoTracking().Select(e => e.Action).Distinct().ToListAsync(ct);
        var groups = actions.Select(a => a.Split('.')[0]).Distinct().Order(StringComparer.Ordinal).ToList();
        return new AuditFacets(types, groups);
    }
}
