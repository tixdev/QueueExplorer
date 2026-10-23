using DynaQueueExplorer.Core.Security;

namespace DynaQueueExplorer.Core.Clusters;

/// <summary>
/// Named selections of queues that span brokers, e.g. "T1 CH": the RabbitMQ vhosts of an environment and the IBM MQ
/// queues of the same environment. They only narrow what the UI shows (and feed per-group trends); access is still
/// decided by RBAC.
/// </summary>
public sealed class QueueGroupsOptions
{
    public const string SectionName = "QueueGroups";

    public List<QueueGroup> Items { get; set; } = [];
}

public sealed class QueueGroup
{
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>A queue belongs to the group when any rule matches it.</summary>
    public List<QueueGroupRule> Rules { get; set; } = [];

    public bool Contains(string broker, string cluster, string vhost, string queue) =>
        Rules.Any(r => r.Matches(broker, cluster, vhost, queue));
}

/// <summary>
/// Glob patterns (<c>*</c>, <c>?</c>, case-insensitive); an unset field matches everything. For RabbitMQ the vhost is
/// the RabbitMQ vhost, for IBM MQ it is the queue manager name.
/// </summary>
public sealed class QueueGroupRule
{
    /// <summary><c>rabbitmq</c>, <c>ibmmq</c>, or unset for every broker.</summary>
    public string? Broker { get; set; }
    public string? Cluster { get; set; }
    public string? Vhost { get; set; }
    public string? Queue { get; set; }

    public bool Matches(string broker, string cluster, string vhost, string queue) =>
        (string.IsNullOrEmpty(Broker) || string.Equals(Broker, broker, StringComparison.OrdinalIgnoreCase)) &&
        Like(Cluster, cluster) && Like(Vhost, vhost) && Like(Queue, queue);

    private static bool Like(string? pattern, string value) =>
        string.IsNullOrEmpty(pattern) || Glob.IsMatch(pattern.ToUpperInvariant(), value.ToUpperInvariant());
}
