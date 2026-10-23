namespace DynaQueueExplorer.Core.Security;

/// <summary>
/// Actions that can be granted to a role. Each action is evaluated at a fixed scope level.
/// </summary>
public static class Permissions
{
    public const string Any = "*";

    public const string OverviewRead = "overview.read";
    public const string NodesRead = "nodes.read";
    public const string QueuesRead = "queues.read";
    public const string ExchangesRead = "exchanges.read";
    public const string ShovelsRead = "shovels.read";
    public const string MessagesBrowse = "messages.browse";
    /// <summary>Remove messages from a queue (move, or stage with removal).</summary>
    public const string MessagesMove = "messages.move";
    /// <summary>Publish into a queue (checked on the destination of move, copy, retry and staging publish).</summary>
    public const string MessagesPublish = "messages.publish";
    public const string MessagesExport = "messages.export";
    public const string StagingRead = "staging.read";
    public const string StagingWrite = "staging.write";
    public const string AuditRead = "audit.read";

    public static readonly IReadOnlyDictionary<string, PermissionScope> Scopes = new Dictionary<string, PermissionScope>
    {
        [OverviewRead] = PermissionScope.Cluster,
        [NodesRead] = PermissionScope.Cluster,
        [QueuesRead] = PermissionScope.Queue,
        [ExchangesRead] = PermissionScope.Vhost,
        [ShovelsRead] = PermissionScope.Vhost,
        [MessagesBrowse] = PermissionScope.Queue,
        [MessagesMove] = PermissionScope.Queue,
        [MessagesPublish] = PermissionScope.Queue,
        [MessagesExport] = PermissionScope.Queue,
        [StagingRead] = PermissionScope.Global,
        [StagingWrite] = PermissionScope.Global,
        [AuditRead] = PermissionScope.Global,
    };

    public static IReadOnlyCollection<string> All => (IReadOnlyCollection<string>)Scopes.Keys;
}

public enum PermissionScope
{
    Global,
    Cluster,
    Vhost,
    Queue,
}
