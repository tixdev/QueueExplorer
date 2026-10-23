using System.Text.Json;
using DynaQueueExplorer.Core.Messages;
using DynaQueueExplorer.NServiceBus;
using DynaQueueExplorer.RabbitMq.Management;

namespace DynaQueueExplorer.Api.Endpoints;

public sealed record SnapshotInfo(DateTimeOffset FetchedAt, int FetchMs, DateTimeOffset? LastErrorAt, string? LastError);

public sealed record OverviewResponse(SnapshotInfo Snapshot, RmqOverview? Overview, IReadOnlyList<RmqNode>? Nodes);

public sealed record QueueDto(
    string Vhost,
    string Name,
    string? Type,
    string? State,
    bool Durable,
    bool AutoDelete,
    bool Exclusive,
    string? Policy,
    long? Messages,
    long? Ready,
    long? Unacked,
    int? Consumers,
    Dictionary<string, JsonElement>? Arguments,
    bool CanBrowse);

public sealed record ExchangeDto(string Vhost, string Name, string Type, bool Durable, bool AutoDelete, bool Internal, Dictionary<string, JsonElement>? Arguments);

public sealed record BindingDto(string Vhost, string Source, string Destination, string DestinationType, string RoutingKey, Dictionary<string, JsonElement>? Arguments);

/// <summary>Everything the topology views need, in one response, already filtered by RBAC.</summary>
public sealed record TopologyResponse(
    SnapshotInfo Snapshot,
    IReadOnlyList<string> Vhosts,
    IReadOnlyList<QueueDto> Queues,
    IReadOnlyList<ExchangeDto> Exchanges,
    IReadOnlyList<BindingDto> Bindings);

public sealed record ShovelDto(
    string Name,
    string? Vhost,
    string? Type,
    string State,
    string? Node,
    string? Timestamp,
    string? Source,
    string? Destination,
    string? SourceUri,
    string? DestinationUri,
    string? Reason,
    JsonElement? Definition);

public sealed record QueueDetailResponse(RmqQueue Queue, IReadOnlyList<BindingDto> Bindings);

public sealed record BrowseRequest(string Vhost, string Queue, int Count = 20, bool AcceptRequeueSideEffects = false);

/// <param name="Key">Stable identity used to select the message for move/copy/export (see MessageKeys).</param>
/// <param name="StagedId">Set for messages in the staging area.</param>
public sealed record BrowsedMessage(
    int Index,
    string Key,
    bool Redelivered,
    string Exchange,
    string RoutingKey,
    long PayloadBytes,
    Dictionary<string, JsonElement> Properties,
    Dictionary<string, JsonElement> Headers,
    string? ContentType,
    FormattedBody Body,
    NServiceBusMessageInfo? NServiceBus,
    long? StagedId = null,
    bool Discarded = false,
    DateTimeOffset? LastPublishedAt = null,
    string? LastPublishedTo = null,
    string? Broker = null,
    DateTimeOffset? EditedAt = null,
    string? EditedBy = null);

/// <param name="DeliveryLimit">Effective delivery limit of a quorum queue; -1 means unlimited.</param>
public sealed record BrowseResponse(string? QueueType, int? DeliveryLimit, long? QueueDepth, IReadOnlyList<string> Warnings, IReadOnlyList<BrowsedMessage> Messages);

public sealed record AuditEntryDto(long Id, DateTimeOffset Timestamp, string User, string Action, string Outcome, string? Cluster, string? Vhost, string? Resource, string? Details);

/// <summary>Which messages of a queue: the first <see cref="Count"/>, or the ones with the given keys.</summary>
public sealed record QueueSelection(string Vhost, string Queue, int? Count = null, List<string>? Keys = null);

public enum TransferMode
{
    Move,
    Copy,
}

/// <param name="RetryToFailedQueue">NServiceBus: send each message back to the queue in its NServiceBus.FailedQ header.</param>
/// <param name="RemoveFailureHeaders">NServiceBus: drop exception/failure headers from the republished message.</param>
public sealed record TransferRequest(
    string Vhost,
    string Queue,
    TransferMode Mode,
    int? Count = null,
    List<string>? Keys = null,
    string? DestinationVhost = null,
    string? DestinationQueue = null,
    bool RetryToFailedQueue = false,
    bool RemoveFailureHeaders = false);

public sealed record StageRequest(string Vhost, string Queue, bool Remove, int? Count = null, List<string>? Keys = null, string? Name = null);

public sealed record StagingBatchDto(
    int Id,
    string Name,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string Origin,
    string? OriginCluster,
    string? OriginVhost,
    string? OriginQueue,
    int Messages,
    int Discarded,
    int Published,
    string Broker);

public sealed record StagingBatchDetail(StagingBatchDto Batch, IReadOnlyList<BrowsedMessage> Messages);

public sealed record DiscardRequest(List<long> Ids, bool Discarded = true);

/// <param name="Ids">Messages to publish; null publishes every message that is not discarded.</param>
public sealed record StagingPublishRequest(
    string Cluster,
    string Vhost,
    string? Queue = null,
    bool RetryToFailedQueue = false,
    bool RemoveFailureHeaders = false,
    List<long>? Ids = null);

public sealed record PublishResult(int Requested, int Published, IReadOnlyList<PublishFailureDto> Failures);

public sealed record PublishFailureDto(string Key, string Reason);

/// <summary>A header after editing: <see cref="Keep"/> leaves the original (typed) value untouched.</summary>
public sealed record HeaderEdit(string Key, string? Value = null, bool Keep = false);

/// <summary>Editable AMQP basic properties; timestamp, user id and cluster id are kept as staged.</summary>
public sealed record PropertiesEdit(
    string? ContentType,
    string? ContentEncoding,
    byte? DeliveryMode,
    byte? Priority,
    string? CorrelationId,
    string? ReplyTo,
    string? Expiration,
    string? MessageId,
    string? Type,
    string? AppId);

/// <param name="Body">New body as UTF-8 text; null keeps the current body.</param>
public sealed record EditStagedMessageRequest(PropertiesEdit Properties, List<HeaderEdit> Headers, string? Body = null);
