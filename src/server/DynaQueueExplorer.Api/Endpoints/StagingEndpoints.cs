using DynaQueueExplorer.Api.Security;
using DynaQueueExplorer.Core.Auditing;
using DynaQueueExplorer.Core.Messages;
using DynaQueueExplorer.Core.Security;
using DynaQueueExplorer.Infrastructure.Persistence;
using DynaQueueExplorer.NServiceBus;
using DynaQueueExplorer.RabbitMq.Amqp;
using DynaQueueExplorer.RabbitMq.Snapshots;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DynaQueueExplorer.Api.Endpoints;

/// <summary>The staging area: messages imported from files or taken from queues, inspected before publishing.</summary>
public static class StagingEndpoints
{
    private const long MaxImportBytes = 200L * 1024 * 1024;

    public static void MapStagingEndpoints(this IEndpointRouteBuilder app)
    {
        var staging = app.MapGroup("/api/staging").RequireAuthorization();
        staging.MapGet("/", List);
        // Our own header check protects against CSRF (see RequireClientHeaderMiddleware); form antiforgery is not used.
        staging.MapPost("/import", Import).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(MaxImportBytes));
        staging.MapGet("/{id:int}", Get);
        staging.MapDelete("/{id:int}", Delete);
        staging.MapPost("/{id:int}/discard", Discard);
        staging.MapGet("/{id:int}/export", Export);
        staging.MapPost("/{id:int}/publish", Publish);
        staging.MapPut("/{id:int}/messages/{messageId:long}", Edit);
        staging.MapPost("/{id:int}/messages/{messageId:long}/revert", Revert);
    }

    private static async Task<IResult> List(string? broker, CurrentUser user, ExplorerDbContext db, CancellationToken ct)
    {
        if (!await Can(user, Permissions.StagingRead, ct))
            return Results.Forbid();

        var query = db.StagingBatches.AsNoTracking();
        if (!string.IsNullOrEmpty(broker))
            query = query.Where(b => b.Broker == broker);
        var batches = await query
            .OrderByDescending(b => b.Id)
            .Select(b => new
            {
                Batch = b,
                Total = b.Messages.Count,
                Discarded = b.Messages.Count(m => m.Discarded),
                Published = b.Messages.Count(m => m.LastPublishedAt != null),
            })
            .ToListAsync(ct);
        return Results.Ok(batches.Select(b => StagingMapper.ToDto(b.Batch, b.Total, b.Discarded, b.Published)));
    }

    private static async Task<IResult> Import(IFormFile file, string? name, CurrentUser user, ExplorerDbContext db, IAuditLog audit,
        TimeProvider time, CancellationToken ct)
    {
        if (!await Can(user, Permissions.StagingWrite, ct))
            return Results.Forbid();

        List<RawMessage> messages;
        MqxManifest manifest;
        try
        {
            await using var stream = file.OpenReadStream();
            (manifest, messages) = await MqxArchive.ReadAsync(stream, ct);
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException)
        {
            return Results.Problem($"'{file.FileName}' is not a valid {MqxArchive.Extension} file: {e.Message}", statusCode: StatusCodes.Status400BadRequest);
        }

        var origin = manifest.Source is { } s ? $"{file.FileName} (from {s.Cluster}/{s.Vhost}/{s.Queue})" : file.FileName;
        var batch = StagingMapper.NewBatch(manifest.Broker ?? BrokerTypes.RabbitMq, name ?? Path.GetFileNameWithoutExtension(file.FileName), user.Username, time.GetUtcNow(), origin,
            manifest.Source?.Cluster, manifest.Source?.Vhost, manifest.Source?.Queue, messages);
        db.StagingBatches.Add(batch);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(new AuditRecord(user.Username, "staging.import", AuditOutcome.Success, Resource: file.FileName,
            Details: $"batch={batch.Id} messages={messages.Count}"), ct);
        return Results.Ok(StagingMapper.ToDto(batch, messages.Count, 0, 0));
    }

    private static async Task<IResult> Get(int id, CurrentUser user, ExplorerDbContext db, CancellationToken ct)
    {
        if (!await Can(user, Permissions.StagingRead, ct))
            return Results.Forbid();

        var batch = await db.StagingBatches.AsNoTracking().Include(b => b.Messages.OrderBy(m => m.Position)).FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null)
            return Results.NotFound();

        var views = batch.Messages.Select(StagingMapper.ToView).ToList();
        var dto = StagingMapper.ToDto(batch, batch.Messages.Count, batch.Messages.Count(m => m.Discarded), batch.Messages.Count(m => m.LastPublishedAt != null));
        return Results.Ok(new StagingBatchDetail(dto, views));
    }

    private static async Task<IResult> Delete(int id, CurrentUser user, ExplorerDbContext db, IAuditLog audit, CancellationToken ct)
    {
        if (!await Can(user, Permissions.StagingWrite, ct))
            return Results.Forbid();

        var deleted = await db.StagingBatches.Where(b => b.Id == id).ExecuteDeleteAsync(ct);
        if (deleted == 0)
            return Results.NotFound();
        await audit.WriteAsync(new AuditRecord(user.Username, "staging.delete", AuditOutcome.Success, Details: $"batch={id}"), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Discard(int id, DiscardRequest request, CurrentUser user, ExplorerDbContext db, CancellationToken ct)
    {
        if (!await Can(user, Permissions.StagingWrite, ct))
            return Results.Forbid();

        var changed = await db.StagedMessages
            .Where(m => m.BatchId == id && request.Ids.Contains(m.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Discarded, request.Discarded), ct);
        return Results.Ok(new { changed });
    }

    private static async Task<IResult> Export(int id, CurrentUser user, ExplorerDbContext db, IAuditLog audit, CancellationToken ct)
    {
        if (!await Can(user, Permissions.StagingRead, ct))
            return Results.Forbid();

        var batch = await db.StagingBatches.AsNoTracking().Include(b => b.Messages.Where(m => !m.Discarded).OrderBy(m => m.Position))
            .FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null)
            return Results.NotFound();

        var file = new MemoryStream();
        await MqxArchive.WriteAsync(file, batch.Messages.Select(StagingMapper.ToRaw).ToList(), user.Username,
            new MqxSource(batch.OriginCluster, batch.OriginVhost, batch.OriginQueue), ct);
        await audit.WriteAsync(new AuditRecord(user.Username, "staging.export", AuditOutcome.Success, Details: $"batch={id} messages={batch.Messages.Count}"), ct);
        file.Position = 0;
        return Results.File(file, "application/zip", $"{MessageOperationEndpoints.SafeFileName(batch.Name)}{MqxArchive.Extension}");
    }

    private static async Task<IResult> Publish(
        int id, StagingPublishRequest request, CurrentUser user, ExplorerDbContext db, MessageOperations operations,
        ClusterSnapshotService snapshots, IAuditLog audit, TimeProvider time, CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        if (!permissions.Allows(Permissions.StagingRead, ResourceRef.Global))
            return Results.Forbid();
        if (!request.RetryToFailedQueue && string.IsNullOrEmpty(request.Queue))
            return Results.Problem("A destination queue is required.", statusCode: StatusCodes.Status400BadRequest);
        if (!request.RetryToFailedQueue && !permissions.Allows(Permissions.MessagesPublish, new ResourceRef(request.Cluster, request.Vhost, request.Queue)))
        {
            await audit.WriteAsync(new AuditRecord(user.Username, Permissions.MessagesPublish, AuditOutcome.Denied, request.Cluster, request.Vhost,
                request.Queue, $"staging batch={id}"), ct);
            return Results.Forbid();
        }

        var batchBroker = await db.StagingBatches.Where(b => b.Id == id).Select(b => b.Broker).FirstOrDefaultAsync(ct);
        if (batchBroker is null)
            return Results.NotFound();
        if (batchBroker != BrokerTypes.RabbitMq)
            return Results.Problem($"Publishing {batchBroker} messages to RabbitMQ is not supported.", statusCode: StatusCodes.Status400BadRequest);

        var query = db.StagedMessages.Where(m => m.BatchId == id);
        query = request.Ids is { Count: > 0 } ids ? query.Where(m => ids.Contains(m.Id)) : query.Where(m => !m.Discarded);
        var staged = await query.OrderBy(m => m.Position).ToListAsync(ct);

        var failures = new List<PublishFailureDto>();
        var toPublish = new List<(StagedMessage Staged, RawMessage Raw, Destination Destination)>();
        foreach (var m in staged)
        {
            var raw = StagingMapper.ToRaw(m);
            var queue = request.RetryToFailedQueue
                ? AmqpProperties.HeaderText(raw.Properties).GetValueOrDefault(NServiceBusHeaders.FailedQueue)
                : request.Queue;
            if (string.IsNullOrEmpty(queue))
                failures.Add(new PublishFailureDto(MessageOperations.KeyOf(raw), "Not a failed NServiceBus message (no NServiceBus.FailedQ header)."));
            else if (!permissions.Allows(Permissions.MessagesPublish, new ResourceRef(request.Cluster, request.Vhost, queue)))
                failures.Add(new PublishFailureDto(MessageOperations.KeyOf(raw), $"Not allowed to publish to '{queue}'."));
            else
                toPublish.Add((m, raw, Destination.Queue(queue)));
        }

        var outcome = await operations.PublishAsync(request.Cluster, request.Vhost, toPublish.Select(p => (p.Raw, p.Destination)).ToList(),
            request.RemoveFailureHeaders ? NServiceBusHeaders.IsFailureHeader : null, ct);

        var failed = outcome.Failures.ToDictionary(f => f.Index, f => f.Reason);
        var now = time.GetUtcNow();
        for (var i = 0; i < toPublish.Count; i++)
        {
            if (failed.TryGetValue(i, out var reason))
            {
                failures.Add(new PublishFailureDto(MessageOperations.KeyOf(toPublish[i].Raw), reason));
                continue;
            }
            toPublish[i].Staged.LastPublishedAt = now;
            toPublish[i].Staged.LastPublishedTo = $"{request.Cluster}/{request.Vhost}/{toPublish[i].Destination.RoutingKey}";
        }
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(new AuditRecord(user.Username, Permissions.MessagesPublish, failures.Count == 0 ? AuditOutcome.Success : AuditOutcome.Failed,
            request.Cluster, request.Vhost, request.RetryToFailedQueue ? "FailedQ" : request.Queue,
            $"staging batch={id} requested={staged.Count} published={outcome.Succeeded} failed={failures.Count}"), ct);
        await MessageOperationEndpoints.RefreshQuietly(snapshots, request.Cluster);
        return Results.Ok(new PublishResult(staged.Count, outcome.Succeeded, failures));
    }

    private static async Task<IResult> Edit(int id, long messageId, EditStagedMessageRequest request, CurrentUser user, ExplorerDbContext db,
        IAuditLog audit, TimeProvider time, CancellationToken ct)
    {
        if (!await Can(user, Permissions.StagingWrite, ct))
            return Results.Forbid();
        var message = await db.StagedMessages.FirstOrDefaultAsync(m => m.Id == messageId && m.BatchId == id, ct);
        if (message is null)
            return Results.NotFound();
        if (message.Broker != BrokerTypes.RabbitMq)
            return Results.Problem($"Editing {message.Broker} messages is not supported yet.", statusCode: StatusCodes.Status400BadRequest);

        var current = StagingMapper.ToRaw(message);
        var originalHeaders = current.Properties.Headers ?? [];
        var headers = new Dictionary<string, System.Text.Json.JsonElement>();
        foreach (var edit in request.Headers)
        {
            if (string.IsNullOrWhiteSpace(edit.Key) || headers.ContainsKey(edit.Key))
                return Results.Problem($"Header names must be unique and not empty ('{edit.Key}').", statusCode: StatusCodes.Status400BadRequest);
            originalHeaders.TryGetValue(edit.Key, out var original);
            bool exists = originalHeaders.ContainsKey(edit.Key);
            try
            {
                headers[edit.Key] = edit.Keep && exists ? original : AmqpValueCodec.FromEditedText(exists ? original : null, edit.Value);
            }
            catch (Exception e) when (e is FormatException or OverflowException)
            {
                return Results.Problem($"Header '{edit.Key}': {e.Message}", statusCode: StatusCodes.Status400BadRequest);
            }
        }

        var p = request.Properties;
        var properties = current.Properties with
        {
            ContentType = Blank(p.ContentType),
            ContentEncoding = Blank(p.ContentEncoding),
            DeliveryMode = p.DeliveryMode,
            Priority = p.Priority,
            CorrelationId = Blank(p.CorrelationId),
            ReplyTo = Blank(p.ReplyTo),
            Expiration = Blank(p.Expiration),
            MessageId = Blank(p.MessageId),
            Type = Blank(p.Type),
            AppId = Blank(p.AppId),
            Headers = headers.Count > 0 ? headers : null,
        };

        // Keep the message as it was staged, once, so the edit can be undone.
        message.OriginalPropertiesJson ??= message.PropertiesJson;
        message.OriginalBody ??= message.Body;
        message.PropertiesJson = StagingMapper.Serialize(properties);
        if (request.Body is not null)
            message.Body = System.Text.Encoding.UTF8.GetBytes(request.Body);
        message.EditedAt = time.GetUtcNow();
        message.EditedBy = user.Username;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(new AuditRecord(user.Username, "staging.edit", AuditOutcome.Success,
            Details: $"batch={id} message={messageId} body={(request.Body is null ? "kept" : "changed")} headers={headers.Count}"), ct);
        return Results.Ok(StagingMapper.ToView(message, message.Position));
    }

    private static async Task<IResult> Revert(int id, long messageId, CurrentUser user, ExplorerDbContext db, IAuditLog audit, CancellationToken ct)
    {
        if (!await Can(user, Permissions.StagingWrite, ct))
            return Results.Forbid();
        var message = await db.StagedMessages.FirstOrDefaultAsync(m => m.Id == messageId && m.BatchId == id, ct);
        if (message is null)
            return Results.NotFound();
        if (message.OriginalPropertiesJson is null || message.OriginalBody is null)
            return Results.Ok(StagingMapper.ToView(message, message.Position));

        message.PropertiesJson = message.OriginalPropertiesJson;
        message.Body = message.OriginalBody;
        message.OriginalPropertiesJson = null;
        message.OriginalBody = null;
        message.EditedAt = null;
        message.EditedBy = null;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(new AuditRecord(user.Username, "staging.revert", AuditOutcome.Success, Details: $"batch={id} message={messageId}"), ct);
        return Results.Ok(StagingMapper.ToView(message, message.Position));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static async Task<bool> Can(CurrentUser user, string action, CancellationToken ct) =>
        (await user.GetPermissionsAsync(ct)).Allows(action, ResourceRef.Global);
}
