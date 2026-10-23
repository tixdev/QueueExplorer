namespace DynaQueueExplorer.IbmMq.Snapshots;

public sealed record MqQueueManagerInfo(string Name, string? Description, int? CommandLevel, string? Version, string? Platform, string? DeadLetterQueue);

/// <param name="Type">local, alias, remote, model or cluster.</param>
/// <param name="Target">Alias: the base queue; remote: "RNAME@RQMNAME via XMITQ".</param>
/// <param name="OldestMessageAgeSeconds">Only when queue monitoring (MONQ) is on.</param>
public sealed record MqQueueInfo(
    string Name,
    string Type,
    bool Transmission,
    long? Depth,
    long? MaxDepth,
    int? OpenInput,
    int? OpenOutput,
    long? Uncommitted,
    bool InhibitGet,
    bool InhibitPut,
    string? Description,
    string? Target,
    DateTimeOffset? LastPut,
    DateTimeOffset? LastGet,
    long? OldestMessageAgeSeconds);

/// <param name="Version">Hash of the content: unchanged data keeps the same value (used as ETag).</param>
/// <param name="Limitations">What the account may not see, with the authority that is missing.</param>
public sealed record MqSnapshot(
    string Cluster,
    DateTimeOffset FetchedAt,
    TimeSpan FetchDuration,
    ulong Version,
    MqQueueManagerInfo QueueManager,
    IReadOnlyList<MqQueueInfo> Queues,
    IReadOnlyList<string> Limitations);
