using DynaQueueExplorer.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DynaQueueExplorer.Infrastructure.Persistence;

/// <summary>
/// Resolves permissions with a single query and caches them briefly, so RBAC costs no database
/// round-trip on most requests. Role changes take effect within <see cref="CacheDuration"/>.
/// </summary>
public sealed class SqlPermissionStore(ExplorerDbContext db, IMemoryCache cache) : IPermissionStore
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public async Task<PermissionSet> GetPermissionsAsync(string username, IReadOnlyCollection<string> groups, CancellationToken cancellationToken)
    {
        var key = $"perm:{username}:{string.Join('|', groups.Order(StringComparer.Ordinal))}";
        if (cache.TryGetValue(key, out PermissionSet? cached) && cached is not null)
            return cached;

        var groupList = groups.ToList();
        var rows = await db.RoleAssignments
            .AsNoTracking()
            .Where(a => (a.SubjectType == SubjectType.User && a.Subject == username)
                        || (a.SubjectType == SubjectType.Group && groupList.Contains(a.Subject)))
            .SelectMany(a => a.Role!.Grants)
            .Select(g => new { g.Action, g.ClusterPattern, g.VhostPattern, g.QueuePattern })
            .Distinct()
            .ToListAsync(cancellationToken);

        var permissions = new PermissionSet(rows.Select(g => new Grant(g.Action, g.ClusterPattern, g.VhostPattern, g.QueuePattern)));
        cache.Set(key, permissions, CacheDuration);
        return permissions;
    }
}
