namespace DynaQueueExplorer.Core.Msmq;

/// <summary>
/// Wire contract between the explorer and the MSMQ agent, a Windows service that reads MSMQ with the native API on
/// behalf of the (cross-platform) explorer. Machine names in the routes are the MSMQ machines the agent may read.
/// </summary>
public static class MsmqAgentApi
{
    /// <summary>Shared secret of the agent, sent by the explorer on every call.</summary>
    public const string KeyHeader = "X-DQE-Agent-Key";

    public static string Queues(string machine) => $"api/machines/{Uri.EscapeDataString(machine)}/queues";
    public static string Browse(string machine) => $"api/machines/{Uri.EscapeDataString(machine)}/browse";
}

public static class MsmqQueueKinds
{
    /// <summary>A private queue of the machine (<c>private$\name</c>).</summary>
    public const string Private = "private";
    /// <summary>Messages on their way to another machine; the name is the destination (<c>host\private$\name</c>).</summary>
    public const string Outgoing = "outgoing";
    /// <summary>The system dead-letter queues: <c>system$;deadxact</c> (transactional) and <c>system$;deadletter</c>.</summary>
    public const string DeadLetter = "deadletter";
}

/// <param name="DepthSource">How depths were read: performance counters (one call) or queue management (one call per active queue).</param>
/// <param name="Limitations">What the agent's account could not read, and what was done instead.</param>
public sealed record MsmqQueueListResponse(
    string Machine,
    DateTimeOffset ReadAt,
    string DepthSource,
    IReadOnlyList<MsmqQueueStatus> Queues,
    IReadOnlyList<string> Limitations);

/// <param name="Name">Private: <c>private$\name</c>; outgoing: <c>host\private$\name</c>; dead-letter: <c>system$;deadxact</c>.</param>
/// <param name="Messages">Null when it could not be read. Inactive queues are empty: MSMQ activates every queue that holds messages.</param>
/// <param name="AtLeast">The count stopped at a limit (dead-letter queues are counted by peeking): there are at least <see cref="Messages"/>.</param>
/// <param name="State">Outgoing queues: connected, waiting, on hold, disconnected…</param>
/// <param name="NextHops">Outgoing queues: addresses MSMQ is trying to deliver to.</param>
public sealed record MsmqQueueStatus(
    string Name,
    string Kind,
    string FormatName,
    long? Messages,
    long? Bytes = null,
    long? JournalMessages = null,
    bool AtLeast = false,
    string? State = null,
    IReadOnlyList<string>? NextHops = null);

public sealed record MsmqBrowseRequest(string Queue, int Count = 20, int MaxBodyBytes = 256 * 1024);

public sealed record MsmqBrowseResponse(string Machine, string Queue, IReadOnlyList<MsmqMessage> Messages);

/// <summary>An MSMQ message as peeked; ids use the System.Messaging text form <c>{guid}\{sequence}</c>.</summary>
/// <param name="Class">Message class: 0 for a normal message; for dead-lettered messages the reason (MQMSG_CLASS_NACK_…).</param>
/// <param name="BodyType">bytes, or string when the sender stored a COM string (decoded here as UTF-8).</param>
/// <param name="ExtensionBase64">The extension property; NServiceBus stores its headers there as XML.</param>
public sealed record MsmqMessage(
    string Id,
    string? CorrelationId,
    string? Label,
    long? LookupId,
    int? Class,
    int? Priority,
    bool? Recoverable,
    bool? UseJournal,
    bool? UseDeadLetter,
    int? AppSpecific,
    DateTimeOffset? SentTime,
    DateTimeOffset? ArrivedTime,
    long? MaxTimeToReachQueueSeconds,
    long? MaxTimeToReceiveSeconds,
    string? ResponseQueue,
    string? AdminQueue,
    string? DestinationQueue,
    string? TransactionId,
    bool? FirstInTransaction,
    bool? LastInTransaction,
    string? SourceMachineId,
    string? SenderId,
    int BodyLength,
    string BodyType,
    string BodyBase64,
    bool BodyTruncated,
    string? ExtensionBase64);
