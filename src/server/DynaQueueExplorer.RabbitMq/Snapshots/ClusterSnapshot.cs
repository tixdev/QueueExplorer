using DynaQueueExplorer.RabbitMq.Management;

namespace DynaQueueExplorer.RabbitMq.Snapshots;

/// <summary>
/// Immutable picture of a cluster, refreshed in the background with a handful of bulk API calls.
/// The two versions change only when the corresponding raw payloads change, so they double as ETags.
/// </summary>
/// <param name="FetchedAt">When the state (depths, consumers, nodes, shovel status) was read.</param>
/// <param name="StructureFetchedAt">When the structure (vhosts, exchanges, bindings, shovel definitions) was read.</param>
public sealed record ClusterSnapshot(
    string Cluster,
    DateTimeOffset FetchedAt,
    DateTimeOffset StructureFetchedAt,
    TimeSpan FetchDuration,
    ulong TopologyVersion,
    ulong MetricsVersion,
    RmqOverview Overview,
    IReadOnlyList<RmqNode> Nodes,
    IReadOnlyList<RmqVhost> Vhosts,
    IReadOnlyList<RmqQueue> Queues,
    IReadOnlyList<RmqExchange> Exchanges,
    IReadOnlyList<RmqBinding> Bindings,
    IReadOnlyList<RmqShovelStatus> ShovelStatuses,
    IReadOnlyList<RmqParameter> ShovelDefinitions);

public sealed record ClusterHealth(
    string Cluster,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastErrorAt,
    string? LastError);
