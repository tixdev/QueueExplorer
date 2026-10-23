namespace DynaQueueExplorer.Core.Security;

/// <summary>A resource an action is performed on. Null parts are not constrained.</summary>
public sealed record ResourceRef(string? Cluster = null, string? Vhost = null, string? Queue = null)
{
    public static readonly ResourceRef Global = new();
}

/// <summary>A single grant: an action (or <c>*</c>) on resources matching the glob patterns.</summary>
public sealed record Grant(string Action, string ClusterPattern = "*", string VhostPattern = "*", string QueuePattern = "*");

/// <summary>
/// The effective permissions of a user. Deny by default: an action is allowed only when a grant matches it.
/// </summary>
public sealed class PermissionSet(IEnumerable<Grant> grants)
{
    public static readonly PermissionSet Empty = new([]);

    public IReadOnlyList<Grant> Grants { get; } = grants.ToList();

    public bool Allows(string action, ResourceRef resource)
    {
        var scope = Permissions.Scopes.GetValueOrDefault(action, PermissionScope.Queue);
        return Grants.Any(g => ActionMatches(g, action) && ResourceMatches(g, resource, scope));
    }

    /// <summary>True if the action is allowed on at least one resource of the cluster (or anywhere, when null).</summary>
    public bool AllowsAnywhere(string action, string? cluster = null) =>
        Grants.Any(g => ActionMatches(g, action) && (cluster is null || Glob.IsMatch(g.ClusterPattern, cluster)));

    public IReadOnlyList<string> ActionsAnywhereIn(string cluster) =>
        Permissions.All.Where(a => Permissions.Scopes[a] != PermissionScope.Global && AllowsAnywhere(a, cluster)).ToList();

    private static bool ActionMatches(Grant grant, string action) =>
        grant.Action == Permissions.Any || string.Equals(grant.Action, action, StringComparison.Ordinal);

    private static bool ResourceMatches(Grant grant, ResourceRef resource, PermissionScope scope)
    {
        if (scope == PermissionScope.Global)
            return true;
        if (!Matches(grant.ClusterPattern, resource.Cluster))
            return false;
        if (scope == PermissionScope.Cluster)
            return true;
        if (!Matches(grant.VhostPattern, resource.Vhost))
            return false;
        return scope == PermissionScope.Vhost || Matches(grant.QueuePattern, resource.Queue);
    }

    private static bool Matches(string pattern, string? value) => value is null || Glob.IsMatch(pattern, value);
}
