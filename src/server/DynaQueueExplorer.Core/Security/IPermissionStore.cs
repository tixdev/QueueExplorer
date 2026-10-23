namespace DynaQueueExplorer.Core.Security;

public interface IPermissionStore
{
    /// <summary>Resolves the grants of every role assigned to the user directly or through one of its groups.</summary>
    Task<PermissionSet> GetPermissionsAsync(string username, IReadOnlyCollection<string> groups, CancellationToken cancellationToken);
}
