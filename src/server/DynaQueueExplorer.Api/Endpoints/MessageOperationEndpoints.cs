using System.Text.Json;
using DynaQueueExplorer.Api.Security;
using DynaQueueExplorer.Core.Auditing;
using DynaQueueExplorer.Core.Messages;
using DynaQueueExplorer.Core.Security;
using DynaQueueExplorer.Infrastructure.Persistence;
using DynaQueueExplorer.NServiceBus;
using DynaQueueExplorer.RabbitMq;
using DynaQueueExplorer.RabbitMq.Amqp;
using DynaQueueExplorer.RabbitMq.Snapshots;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.Api.Endpoints;

/// <summary>Operations that read messages over AMQP: move, copy, NServiceBus retry, export to file, stage.</summary>
public static class MessageOperationEndpoints
{
    public static void MapMessageOperationEndpoints(this IEndpointRouteBuilder app)
    {
        var messages = app.MapGroup("/api/clusters/{cluster}/messages").RequireAuthorization();
        messages.MapPost("/transfer", Transfer);
        messages.MapPost("/export", Export);
        messages.MapPost("/stage", Stage);
    }

    private static async Task<IResult> Transfer(
        string cluster, TransferRequest request, CurrentUser user, MessageOperations operations, ClusterSnapshotService snapshots,
        IAuditLog audit, IOptions<RabbitMqOptions> options, CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        var action = request.RetryToFailedQueue ? "messages.retry" : request.Mode == TransferMode.Move ? Permissions.MessagesMove : "messages.copy";
        var sourceAction = request.Mode == TransferMode.Move ? Permissions.MessagesMove : Permissions.MessagesBrowse;
        var destinationVhost = request.RetryToFailedQueue ? request.Vhost : request.DestinationVhost ?? request.Vhost;

        if (!request.RetryToFailedQueue && string.IsNullOrEmpty(request.DestinationQueue))
            return Results.Problem("A destination queue is required.", statusCode: StatusCodes.Status400BadRequest);
        if (!request.RetryToFailedQueue && destinationVhost == request.Vhost && request.DestinationQueue == request.Queue)
            return Results.Problem("Source and destination are the same queue.", statusCode: StatusCodes.Status400BadRequest);
        if (!permissions.Allows(sourceAction, new ResourceRef(cluster, request.Vhost, request.Queue))
            || (!request.RetryToFailedQueue && !permissions.Allows(Permissions.MessagesPublish, new ResourceRef(cluster, destinationVhost, request.DestinationQueue))))
        {
            await audit.WriteAsync(new AuditRecord(user.Username, action, AuditOutcome.Denied, cluster, request.Vhost, request.Queue,
                $"to={destinationVhost}/{request.DestinationQueue}"), ct);
            return Results.Forbid();
        }
        if (Selection(request.Count, request.Keys, options.Value) is not { } selection)
            return Results.Problem("Select messages or give a count.", statusCode: StatusCodes.Status400BadRequest);

        MessageRoute RouteOf(RawMessage message)
        {
            if (!request.RetryToFailedQueue)
                return MessageRoute.To(Destination.Queue(request.DestinationQueue!));

            var failedQueue = AmqpProperties.HeaderText(message.Properties).GetValueOrDefault(NServiceBusHeaders.FailedQueue);
            if (string.IsNullOrEmpty(failedQueue))
                return MessageRoute.None("Not a failed NServiceBus message (no NServiceBus.FailedQ header).");
            return permissions.Allows(Permissions.MessagesPublish, new ResourceRef(cluster, destinationVhost, failedQueue))
                ? MessageRoute.To(Destination.Queue(failedQueue))
                : MessageRoute.None($"Not allowed to publish to '{failedQueue}'.");
        }

        var outcome = await operations.TransferAsync(cluster, request.Vhost, request.Queue, selection, request.Mode == TransferMode.Move,
            destinationVhost, RouteOf, request.RemoveFailureHeaders ? NServiceBusHeaders.IsFailureHeader : null, ct);

        await audit.WriteAsync(new AuditRecord(user.Username, action, outcome.Failures.Count == 0 ? AuditOutcome.Success : AuditOutcome.Failed,
            cluster, request.Vhost, request.Queue,
            $"to={(request.RetryToFailedQueue ? "FailedQ" : $"{destinationVhost}/{request.DestinationQueue}")} selected={outcome.Selected} " +
            $"done={outcome.Succeeded} failed={outcome.Failures.Count}"), ct);
        await RefreshQuietly(snapshots, cluster);
        return Results.Ok(outcome);
    }

    private static async Task<IResult> Export(
        string cluster, QueueSelection request, CurrentUser user, MessageOperations operations, IAuditLog audit,
        IOptions<RabbitMqOptions> options, CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        if (!permissions.Allows(Permissions.MessagesExport, new ResourceRef(cluster, request.Vhost, request.Queue)))
        {
            await audit.WriteAsync(new AuditRecord(user.Username, Permissions.MessagesExport, AuditOutcome.Denied, cluster, request.Vhost, request.Queue), ct);
            return Results.Forbid();
        }
        if (Selection(request.Count, request.Keys, options.Value) is not { } selection)
            return Results.Problem("Select messages or give a count.", statusCode: StatusCodes.Status400BadRequest);

        var file = new MemoryStream();
        var count = await operations.TakeAsync(cluster, request.Vhost, request.Queue, selection, remove: false,
            messages => MqxArchive.WriteAsync(file, messages, user.Username, new MqxSource(cluster, request.Vhost, request.Queue), ct), ct);

        await audit.WriteAsync(new AuditRecord(user.Username, Permissions.MessagesExport, AuditOutcome.Success, cluster, request.Vhost, request.Queue,
            $"exported={count}"), ct);
        file.Position = 0;
        return Results.File(file, "application/zip", $"{SafeFileName(request.Queue)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}{MqxArchive.Extension}");
    }

    private static async Task<IResult> Stage(
        string cluster, StageRequest request, CurrentUser user, MessageOperations operations, ExplorerDbContext db,
        ClusterSnapshotService snapshots, IAuditLog audit, IOptions<RabbitMqOptions> options, TimeProvider time, CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        var sourceAction = request.Remove ? Permissions.MessagesMove : Permissions.MessagesBrowse;
        if (!permissions.Allows(Permissions.StagingWrite, ResourceRef.Global)
            || !permissions.Allows(sourceAction, new ResourceRef(cluster, request.Vhost, request.Queue)))
        {
            await audit.WriteAsync(new AuditRecord(user.Username, "staging.stage", AuditOutcome.Denied, cluster, request.Vhost, request.Queue), ct);
            return Results.Forbid();
        }
        if (Selection(request.Count, request.Keys, options.Value) is not { } selection)
            return Results.Problem("Select messages or give a count.", statusCode: StatusCodes.Status400BadRequest);

        StagingBatch? batch = null;
        // The batch is committed before the source is acknowledged: a failure leaves the messages in the queue.
        await operations.TakeAsync(cluster, request.Vhost, request.Queue, selection, request.Remove, async messages =>
        {
            batch = StagingMapper.NewBatch(BrokerTypes.RabbitMq, request.Name ?? $"{request.Queue} ({request.Vhost})", user.Username, time.GetUtcNow(),
                $"{cluster}/{request.Vhost}/{request.Queue}", cluster, request.Vhost, request.Queue, messages);
            db.StagingBatches.Add(batch);
            await db.SaveChangesAsync(ct);
        }, ct);

        await audit.WriteAsync(new AuditRecord(user.Username, request.Remove ? "staging.stage-remove" : "staging.stage", AuditOutcome.Success,
            cluster, request.Vhost, request.Queue, $"batch={batch!.Id} messages={batch.Messages.Count}"), ct);
        if (request.Remove)
            await RefreshQuietly(snapshots, cluster);
        return Results.Ok(StagingMapper.ToDto(batch, batch.Messages.Count, 0, 0));
    }

    internal static MessageSelection? Selection(int? count, List<string>? keys, RabbitMqOptions options) =>
        keys is { Count: > 0 } ? new MessageSelection(options.MaxMessagesPerOperation, keys.ToHashSet(StringComparer.Ordinal))
        : count is > 0 ? new MessageSelection(Math.Min(count.Value, options.MaxMessagesPerOperation))
        : null;

    /// <summary>Lets the next list request show the new queue depths without waiting for the next poll.</summary>
    internal static async Task RefreshQuietly(ClusterSnapshotService snapshots, string cluster)
    {
        try
        {
            await snapshots.RefreshAsync(cluster, CancellationToken.None);
        }
        catch
        {
            // The background refresh will retry; the operation itself succeeded.
        }
    }

    internal static string SafeFileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '/' ? '_' : c));
}

internal static class StagingMapper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static StagingBatch NewBatch(string broker, string name, string user, DateTimeOffset now, string origin,
        string? cluster, string? vhost, string? queue, IReadOnlyList<RawMessage> messages) => new()
    {
        Broker = broker,
        Name = name,
        CreatedBy = user,
        CreatedAt = now,
        Origin = origin,
        OriginCluster = cluster,
        OriginVhost = vhost,
        OriginQueue = queue,
        Messages = messages.Select((m, i) => new StagedMessage
        {
            Position = i,
            Broker = broker,
            Exchange = m.Exchange,
            RoutingKey = m.RoutingKey,
            PropertiesJson = Serialize(m.Properties),
            Body = m.Body,
        }).ToList(),
    };

    public static RawMessage ToRaw(StagedMessage m) =>
        new(m.Exchange, m.RoutingKey, false, JsonSerializer.Deserialize<StoredProperties>(m.PropertiesJson, Json)!, m.Body);

    public static StagingBatchDto ToDto(StagingBatch b, int messages, int discarded, int published) =>
        new(b.Id, b.Name, b.CreatedBy, b.CreatedAt, b.Origin, b.OriginCluster, b.OriginVhost, b.OriginQueue, messages, discarded, published, b.Broker);

    public static string Serialize(StoredProperties properties) => JsonSerializer.Serialize(properties, Json);

    public static BrowsedMessage ToView(StagedMessage m, int index) =>
        MessageViews.FromRaw(index, ToRaw(m), m.Id, m.Discarded, m.LastPublishedAt, m.LastPublishedTo, m.Broker, m.EditedAt, m.EditedBy);
}
