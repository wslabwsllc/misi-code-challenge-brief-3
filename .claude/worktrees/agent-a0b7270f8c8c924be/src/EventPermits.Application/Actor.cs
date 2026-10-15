using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace EventPermits.Application;

/// <summary>The authenticated caller, always loaded from trusted server state (never from the client).</summary>
public sealed record Actor(string UserId, string DisplayName, IReadOnlySet<string> Roles)
{
    public static readonly Actor System = new("system", "System", new HashSet<string>());

    public bool Has(string role) => Roles.Contains(role);

    public bool Can(string capability) => Cap.Allows(capability, Roles);

    /// <summary>Throws 403 when the caller's roles do not grant <paramref name="capability"/>.</summary>
    public void Require(string capability)
    {
        if (!Can(capability)) throw new ForbiddenException();
    }
}

public sealed class ActorResolver(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>
    /// Resolves the caller from the database for every command and read, not from a cached Blazor circuit,
    /// so deactivation and role changes apply immediately (E1-03 AC3).
    /// </summary>
    public async Task<Actor> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) throw new ForbiddenException("You are not signed in.", "NOT_SIGNED_IN");
        return await ResolveAsync(userId, ct);
    }

    public async Task<Actor> ResolveAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive) throw new ForbiddenException("Your account is not active.", "ACCOUNT_INACTIVE");
        var roles = await (from ur in db.UserRoles
                           join r in db.Roles on ur.RoleId equals r.Id
                           where ur.UserId == userId
                           select r.Name!).ToListAsync(ct);
        return new Actor(user.Id, user.DisplayName, roles.ToHashSet());
    }
}
