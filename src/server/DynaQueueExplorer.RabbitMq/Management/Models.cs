using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynaQueueExplorer.RabbitMq.Management;

// Shapes of the RabbitMQ management HTTP API. Only the fields the explorer uses are mapped;
// the list endpoints are called with a matching `columns` filter to keep payloads small.

public sealed record RmqRate(double Rate);

public sealed record RmqMessageStats(
    RmqRate? PublishDetails = null,
    RmqRate? DeliverGetDetails = null,
    RmqRate? AckDetails = null,
    RmqRate? RedeliverDetails = null);

public sealed record RmqQueueTotals(long? Messages = null, long? MessagesReady = null, long? MessagesUnacknowledged = null);

public sealed record RmqObjectTotals(int? Connections = null, int? Channels = null, int? Exchanges = null, int? Queues = null, int? Consumers = null);

public sealed record RmqOverview(
    string? ClusterName = null,
    string? RabbitmqVersion = null,
    string? ErlangVersion = null,
    string? ProductName = null,
    string? ProductVersion = null,
    RmqQueueTotals? QueueTotals = null,
    RmqObjectTotals? ObjectTotals = null,
    RmqMessageStats? MessageStats = null);

public sealed record RmqNode(
    string Name,
    string? Type = null,
    bool Running = false,
    long? Uptime = null,
    int? Processors = null,
    long? MemUsed = null,
    long? MemLimit = null,
    bool? MemAlarm = null,
    long? DiskFree = null,
    long? DiskFreeLimit = null,
    bool? DiskFreeAlarm = null,
    long? FdUsed = null,
    long? FdTotal = null,
    long? SocketsUsed = null,
    long? SocketsTotal = null,
    long? ProcUsed = null,
    long? ProcTotal = null,
    long? RunQueue = null);

public sealed record RmqVhost(string Name, string? Description = null);

public sealed record RmqQueue(
    string Name,
    string Vhost,
    string? Type = null,
    string? State = null,
    bool? Durable = null,
    bool? AutoDelete = null,
    bool? Exclusive = null,
    Dictionary<string, JsonElement>? Arguments = null,
    string? Policy = null,
    long? Messages = null,
    long? MessagesReady = null,
    long? MessagesUnacknowledged = null,
    int? Consumers = null,
    RmqMessageStats? MessageStats = null,
    [property: JsonConverter(typeof(DeliveryLimitConverter))] int? DeliveryLimit = null)
{
    /// <summary>
    /// Whether requeueing (e.g. when browsing) can end with messages dead-lettered or dropped.
    /// Only meaningful on a live single-queue read: list calls use disable_stats, which omits the limit.
    /// </summary>
    public bool HasLowDeliveryLimit => Type == "quorum" && DeliveryLimit is >= 0 and < SafeDeliveryLimit;

    /// <summary>NServiceBus uses 100000 as "practically unlimited"; anything this high is out of reach of browsing.</summary>
    public const int SafeDeliveryLimit = 10_000;
}

public sealed record RmqQueueRef(string Name, string Vhost);

/// <summary>One consumer; only the queue it consumes from is mapped.</summary>
public sealed record RmqConsumer(RmqQueueRef Queue);

public sealed record RmqExchange(
    string Name,
    string Vhost,
    string Type,
    bool? Durable = null,
    bool? AutoDelete = null,
    bool? Internal = null,
    Dictionary<string, JsonElement>? Arguments = null,
    string? Policy = null);

public sealed record RmqBinding(
    string Source,
    string Vhost,
    string Destination,
    string DestinationType,
    string RoutingKey,
    Dictionary<string, JsonElement>? Arguments = null,
    string? PropertiesKey = null);

/// <summary>Runtime status of a shovel (static or dynamic). Fields differ by shovel type and state.</summary>
public sealed record RmqShovelStatus(
    string Name,
    string? Vhost = null,
    string? Type = null,
    string? State = null,
    string? Node = null,
    string? Timestamp = null,
    string? SrcUri = null,
    string? DestUri = null,
    string? SrcQueue = null,
    string? DestQueue = null,
    string? SrcExchange = null,
    string? SrcExchangeKey = null,
    string? DestExchange = null,
    string? DestExchangeKey = null,
    string? SrcProtocol = null,
    string? DestProtocol = null,
    string? BlockedStatus = null,
    JsonElement? Reason = null);

/// <summary>A runtime parameter; for dynamic shovels <see cref="Value"/> holds the definition.</summary>
public sealed record RmqParameter(string Vhost, string Component, string Name, JsonElement Value);

public sealed record RmqGetRequest(int Count, string Ackmode, string Encoding, int Truncate);

public sealed record RmqGetMessage(
    long PayloadBytes,
    bool Redelivered,
    string Exchange,
    string RoutingKey,
    long? MessageCount,
    Dictionary<string, JsonElement>? Properties,
    string Payload,
    string PayloadEncoding);

/// <summary>A page of a filtered list call (the management API pages results when a name filter is used).</summary>
public sealed record RmqPage<T>(List<T> Items, int? FilteredCount = null);

public sealed record RmqQueueDepth(string Name, string Vhost, long? Messages = null);

/// <summary>The management API's view of the service account; its tags decide what it can see.</summary>
public sealed record RmqWhoAmI(string Name, List<string>? Tags = null)
{
    /// <summary>Without monitoring/administrator, RabbitMQ only lists the account's own consumers and connections.</summary>
    public bool SeesEveryConsumer => Tags is not null && (Tags.Contains("monitoring") || Tags.Contains("administrator"));
}
