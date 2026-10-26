using DynaQueueExplorer.IbmMq.Client;
using DynaQueueExplorer.IbmMq.Snapshots;
using DynaQueueExplorer.Core.Msmq;
using DynaQueueExplorer.Msmq;
using System.Text.Json;
using DynaQueueExplorer.Api.Http;
using DynaQueueExplorer.Api.Security;
using DynaQueueExplorer.Core.Auditing;
using DynaQueueExplorer.Core.Messages;
using DynaQueueExplorer.Core.Security;
using DynaQueueExplorer.NServiceBus;
using DynaQueueExplorer.RabbitMq;
using DynaQueueExplorer.RabbitMq.Management;
using DynaQueueExplorer.RabbitMq.Snapshots;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.Api.Endpoints;

public static class ClusterEndpoints
{
    public const string QuorumRequeueConfirmation = "quorum-requeue-confirmation-required";
    private const int MaxBrowseCount = 200;

    public static void MapClusterEndpoints(this IEndpointRouteBuilder app)
    {
        var cluster = app.MapGroup("/api/clusters/{cluster}").RequireAuthorization();

        cluster.MapGet("/overview", GetOverview);
        cluster.MapGet("/topology", GetTopology);
        cluster.MapGet("/shovels", GetShovels).RabbitMqOnly("Shovels");
        cluster.MapGet("/queue", GetQueue).RabbitMqOnly("Live queue details");
        cluster.MapPost("/refresh", Refresh);
        cluster.MapPost("/messages/browse", Browse);
    }

    private static async Task<IResult> GetOverview(
        string cluster, HttpContext http, CurrentUser user, ClusterSnapshotService snapshots, MqSnapshotService mq, MsmqSnapshotService msmq,
        MsmqAgentClients agents, ConditionalJson conditional, CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        var canOverview = permissions.Allows(Permissions.OverviewRead, new ResourceRef(cluster));
        var canNodes = permissions.Allows(Permissions.NodesRead, new ResourceRef(cluster));
        if (!canOverview && !canNodes)
            return NotFoundOrForbidden(permissions, cluster);

        if (msmq.Exists(cluster))
        {
            var s = await msmq.GetSnapshotAsync(cluster, ct);
            var options = agents.Options(cluster);
            // Local queues only: outgoing queues hold messages on their way to other machines.
            var local = s.Queues.Where(q => q.Kind != MsmqQueueKinds.Outgoing).ToList();
            return conditional.Respond(http, "overview", cluster, s.Version, await user.GetPermissionFingerprintAsync(ct), () =>
                new OverviewResponse(MsmqInfo(msmq, s), canOverview
                    ? new RmqOverview(s.Machine, null, null, "MSMQ", null,
                        new RmqQueueTotals(local.Sum(q => q.Messages ?? 0), local.Sum(q => q.Messages ?? 0), 0),
                        new RmqObjectTotals(Queues: local.Count))
                    : null, null, null, new MsmqMachineInfo(s.Machine, options.Host!, options.AgentUrl!.Authority, s.DepthSource)));
        }

        if (mq.Exists(cluster))
        {
            var m = await mq.GetSnapshotAsync(cluster, ct);
            return conditional.Respond(http, "overview", cluster, m.Version, await user.GetPermissionFingerprintAsync(ct), () =>
                new OverviewResponse(MqInfo(mq, m), canOverview
                    ? new RmqOverview(m.QueueManager.Name, null, null, "IBM MQ", m.QueueManager.Version ?? $"command level {m.QueueManager.CommandLevel}",
                        new RmqQueueTotals(m.Queues.Sum(q => q.Depth ?? 0), m.Queues.Sum(q => q.Depth ?? 0), m.Queues.Sum(q => q.Uncommitted ?? 0)),
                        new RmqObjectTotals(Queues: m.Queues.Count))
                    : null, null, m.QueueManager));
        }

        var snapshot = await snapshots.GetSnapshotAsync(cluster, ct);
        return conditional.Respond(http, "overview", cluster, snapshot.MetricsVersion, await user.GetPermissionFingerprintAsync(ct), () =>
            new OverviewResponse(Info(snapshots, snapshot), canOverview ? snapshot.Overview : null, canNodes ? snapshot.Nodes : null));
    }

    private static async Task<IResult> GetTopology(
        string cluster, HttpContext http, CurrentUser user, ClusterSnapshotService snapshots, MqSnapshotService mq, MsmqSnapshotService msmq,
        ConditionalJson conditional, CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        if (!permissions.AllowsAnywhere(Permissions.QueuesRead, cluster) && !permissions.AllowsAnywhere(Permissions.ExchangesRead, cluster))
            return NotFoundOrForbidden(permissions, cluster);

        if (msmq.Exists(cluster))
        {
            var s = await msmq.GetSnapshotAsync(cluster, ct);
            return conditional.Respond(http, "topology", cluster, s.Version, await user.GetPermissionFingerprintAsync(ct), () => BuildMsmqTopology(msmq, s, permissions));
        }

        if (mq.Exists(cluster))
        {
            var m = await mq.GetSnapshotAsync(cluster, ct);
            return conditional.Respond(http, "topology", cluster, m.Version, await user.GetPermissionFingerprintAsync(ct), () => BuildMqTopology(mq, m, permissions));
        }

        var snapshot = await snapshots.GetSnapshotAsync(cluster, ct);
        return conditional.Respond(http, "topology", cluster, snapshot.TopologyVersion, await user.GetPermissionFingerprintAsync(ct),
            () => BuildTopology(snapshots, snapshot, permissions));
    }

    /// <summary>IBM MQ queues in the same shape as RabbitMQ's; the queue manager plays the role of the vhost (also for RBAC).</summary>
    private static TopologyResponse BuildMqTopology(MqSnapshotService mq, MqSnapshot m, PermissionSet permissions)
    {
        var qmgr = m.QueueManager.Name;
        var queues = m.Queues
            .Where(q => permissions.Allows(Permissions.QueuesRead, new ResourceRef(m.Cluster, qmgr, q.Name)))
            .Select(q => new QueueDto(qmgr, q.Name, q.Transmission ? "xmitq" : q.Type, q.InhibitGet ? "get inhibited" : q.InhibitPut ? "put inhibited" : "available",
                true, false, false, null, q.Depth, q.Depth, q.Uncommitted, q.OpenInput, null,
                q.Type is "local" or "alias" && permissions.Allows(Permissions.MessagesBrowse, new ResourceRef(m.Cluster, qmgr, q.Name)),
                q.MaxDepth, q.OpenOutput, q.InhibitGet, q.InhibitPut, q.Target, q.Description, q.LastPut, q.LastGet, q.OldestMessageAgeSeconds, q.Transmission))
            .ToList();
        return new TopologyResponse(MqInfo(mq, m), queues.Count > 0 ? [qmgr] : [], queues, [], []);
    }

    /// <summary>
    /// MSMQ queues in the same shape as RabbitMQ's; the machine plays the role of the vhost (also for RBAC). Outgoing
    /// queues cannot be browsed (their messages are on their way to another machine).
    /// </summary>
    private static TopologyResponse BuildMsmqTopology(MsmqSnapshotService msmq, MsmqSnapshot s, PermissionSet permissions)
    {
        var machine = s.Machine;
        var queues = s.Queues
            .Where(q => permissions.Allows(Permissions.QueuesRead, new ResourceRef(s.Cluster, machine, q.Name)))
            .Select(q => new QueueDto(machine, q.Name, q.Kind, q.State ?? (q.Messages is null ? "unknown" : "available"),
                true, false, false, null, q.Messages, q.Messages, null, null, null,
                q.Kind != MsmqQueueKinds.Outgoing && permissions.Allows(Permissions.MessagesBrowse, new ResourceRef(s.Cluster, machine, q.Name)),
                Bytes: q.Bytes, JournalMessages: q.JournalMessages, AtLeast: q.AtLeast ? true : null, NextHops: q.NextHops is { Count: > 0 } ? q.NextHops : null))
            .ToList();
        return new TopologyResponse(MsmqInfo(msmq, s), queues.Count > 0 ? [machine] : [], queues, [], []);
    }

    private static SnapshotInfo MsmqInfo(MsmqSnapshotService msmq, MsmqSnapshot s)
    {
        var (lastErrorAt, lastError) = msmq.GetHealth(s.Cluster);
        return new SnapshotInfo(s.FetchedAt, (int)s.FetchDuration.TotalMilliseconds, lastErrorAt, lastError, s.Limitations);
    }

    private static SnapshotInfo MqInfo(MqSnapshotService mq, MqSnapshot m)
    {
        var (lastErrorAt, lastError) = mq.GetHealth(m.Cluster);
        return new SnapshotInfo(m.FetchedAt, (int)m.FetchDuration.TotalMilliseconds, lastErrorAt, lastError, m.Limitations);
    }

    private static TopologyResponse BuildTopology(ClusterSnapshotService snapshots, ClusterSnapshot snapshot, PermissionSet permissions)
    {
        var cluster = snapshot.Cluster;
        bool CanReadQueue(string vhost, string name) => permissions.Allows(Permissions.QueuesRead, new ResourceRef(cluster, vhost, name));
        bool CanReadExchanges(string vhost) => permissions.Allows(Permissions.ExchangesRead, new ResourceRef(cluster, vhost));

        var queues = snapshot.Queues
            .Where(q => CanReadQueue(q.Vhost, q.Name))
            .Select(q => new QueueDto(q.Vhost, q.Name, q.Type, q.State, q.Durable ?? false, q.AutoDelete ?? false, q.Exclusive ?? false, q.Policy,
                q.Messages, q.MessagesReady, q.MessagesUnacknowledged, q.Consumers, q.Arguments,
                permissions.Allows(Permissions.MessagesBrowse, new ResourceRef(cluster, q.Vhost, q.Name))))
            .ToList();

        var exchanges = snapshot.Exchanges
            .Where(e => CanReadExchanges(e.Vhost))
            .Select(e => new ExchangeDto(e.Vhost, e.Name, e.Type, e.Durable ?? false, e.AutoDelete ?? false, e.Internal ?? false, e.Arguments))
            .ToList();

        // Default-exchange bindings are implicit (one per queue) and only add noise.
        var bindings = snapshot.Bindings
            .Where(b => b.Source.Length > 0 && CanReadExchanges(b.Vhost))
            .Where(b => b.DestinationType == "queue" ? CanReadQueue(b.Vhost, b.Destination) : CanReadExchanges(b.Vhost))
            .Select(ToDto)
            .ToList();

        var vhosts = snapshot.Vhosts
            .Select(v => v.Name)
            .Where(v => queues.Any(q => q.Vhost == v) || exchanges.Any(e => e.Vhost == v))
            .ToList();

        return new TopologyResponse(Info(snapshots, snapshot), vhosts, queues, exchanges, bindings);
    }

    private static async Task<IResult> GetShovels(
        string cluster, HttpContext http, CurrentUser user, ClusterSnapshotService snapshots, ConditionalJson conditional, CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        if (!permissions.AllowsAnywhere(Permissions.ShovelsRead, cluster))
            return NotFoundOrForbidden(permissions, cluster);

        var snapshot = await snapshots.GetSnapshotAsync(cluster, ct);
        return conditional.Respond(http, "shovels", cluster, snapshot.TopologyVersion, await user.GetPermissionFingerprintAsync(ct), () =>
        {
            // Static shovels have no vhost: they require a grant on every vhost ("*" matches the empty string).
            bool CanRead(string? vhost) => permissions.Allows(Permissions.ShovelsRead, new ResourceRef(cluster, vhost ?? string.Empty));

            return ShovelViews.Merge(snapshot)
                .Where(s => CanRead(s.Vhost))
                .Select(s =>
                {
                    var status = s.Status;
                    JsonElement? definition = s.Definition is { } d ? CredentialMasking.MaskJson(d) : null;
                    return new ShovelDto(
                        s.Name,
                        s.Vhost.Length > 0 ? s.Vhost : null,
                        status?.Type ?? "dynamic",
                        s.State,
                        status?.Node,
                        status?.Timestamp,
                        Endpoint(s.SrcQueue, s.SrcExchange, s.SrcExchangeKey),
                        Endpoint(s.DestQueue, s.DestExchange, s.DestExchangeKey),
                        // Credentials never leave the server: URIs from the status are masked, those of the definition already are.
                        CredentialMasking.MaskUri(status?.SrcUri) ?? ShovelViews.Field(definition, "src-uri"),
                        CredentialMasking.MaskUri(status?.DestUri) ?? ShovelViews.Field(definition, "dest-uri"),
                        status?.Reason is { } reason ? (reason.ValueKind == JsonValueKind.String ? reason.GetString() : reason.GetRawText()) : null,
                        definition);
                })
                .OrderBy(s => s.Vhost, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal)
                .ToList();
        });
    }

    /// <summary>Live details of one queue (including message rates) plus its bindings from the snapshot.</summary>
    private static async Task<IResult> GetQueue(
        string cluster, string vhost, string name, CurrentUser user, ClusterClients clients, ClusterSnapshotService snapshots, CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        if (!permissions.Allows(Permissions.QueuesRead, new ResourceRef(cluster, vhost, name)))
            return NotFoundOrForbidden(permissions, cluster);

        var queue = clients.Get(cluster).GetQueueAsync(vhost, name, ct);
        var snapshot = await snapshots.GetSnapshotAsync(cluster, ct);
        var bindings = permissions.Allows(Permissions.ExchangesRead, new ResourceRef(cluster, vhost))
            ? snapshot.Bindings.Where(b => b.Vhost == vhost && b.Source.Length > 0 && b.DestinationType == "queue" && b.Destination == name).Select(ToDto).ToList()
            : [];
        return Results.Ok(new QueueDetailResponse(await queue, bindings));
    }

    private static async Task<IResult> Refresh(string cluster, CurrentUser user, ClusterSnapshotService snapshots, MqSnapshotService mq, MsmqSnapshotService msmq,
        CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        if (permissions.ActionsAnywhereIn(cluster).Count == 0)
            return NotFoundOrForbidden(permissions, cluster);
        if (msmq.Exists(cluster))
            return Results.Ok(MsmqInfo(msmq, await msmq.RefreshAsync(cluster, ct)));
        if (mq.Exists(cluster))
            return Results.Ok(MqInfo(mq, await mq.RefreshAsync(cluster, ct)));

        // A manual refresh also re-reads the structure (exchanges, bindings…), which is otherwise read less often.
        var snapshot = await snapshots.RefreshAsync(cluster, ct, includeStructure: true);
        return Results.Ok(Info(snapshots, snapshot));
    }

    private static async Task<IResult> Browse(
        string cluster,
        BrowseRequest request,
        CurrentUser user,
        ClusterClients clients,
        MqSnapshotService mq,
        MqBrowser mqBrowser,
        MsmqSnapshotService msmq,
        MsmqAgentClients agents,
        IOptions<MsmqOptions> msmqOptions,
        IAuditLog audit,
        IOptions<RabbitMqOptions> options,
        CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        if (!permissions.Allows(Permissions.MessagesBrowse, new ResourceRef(cluster, request.Vhost, request.Queue)))
        {
            await audit.WriteAsync(new AuditRecord(user.Username, Permissions.MessagesBrowse, AuditOutcome.Denied, cluster, request.Vhost, request.Queue), ct);
            return NotFoundOrForbidden(permissions, cluster);
        }

        if (msmq.Exists(cluster))
        {
            // MSMQ peeking is non-destructive too; the agent only accepts the machine's own queues (not outgoing ones).
            var response = await agents.BrowseAsync(cluster, new MsmqBrowseRequest(request.Queue, Math.Clamp(request.Count, 1, MaxBrowseCount),
                msmqOptions.Value.BrowseTruncateBytes), ct);
            var depth = (await msmq.GetSnapshotAsync(cluster, ct)).Queues.FirstOrDefault(q => q.Name.Equals(request.Queue, StringComparison.OrdinalIgnoreCase))?.Messages;
            await audit.WriteAsync(new AuditRecord(user.Username, Permissions.MessagesBrowse, AuditOutcome.Success, cluster, request.Vhost, request.Queue,
                $"returned={response.Messages.Count}"), ct);
            return Results.Ok(new BrowseResponse("msmq", null, depth, [], response.Messages.Select((m, i) => MessageViews.FromMsmq(i, m)).ToList()));
        }

        if (mq.Exists(cluster))
        {
            // IBM MQ browsing is non-destructive: no confirmation, no requeue side effects.
            var mqMessages = await mqBrowser.BrowseAsync(cluster, request.Queue, Math.Clamp(request.Count, 1, MaxBrowseCount), ct);
            var depth = (await mq.GetSnapshotAsync(cluster, ct)).Queues.FirstOrDefault(q => q.Name == request.Queue)?.Depth;
            await audit.WriteAsync(new AuditRecord(user.Username, Permissions.MessagesBrowse, AuditOutcome.Success, cluster, request.Vhost, request.Queue,
                $"returned={mqMessages.Count}"), ct);
            return Results.Ok(new BrowseResponse("ibmmq", null, depth, [], mqMessages.Select((m, i) => MessageViews.FromMq(i, m)).ToList()));
        }

        // Read live, not from the snapshot: whether requeueing can lose messages depends on the effective
        // delivery limit (arguments, policies, broker defaults), which only RabbitMQ resolves authoritatively.
        var client = clients.Get(cluster);
        var queue = await client.GetQueueBrowseSafetyAsync(request.Vhost, request.Queue, ct);

        if (queue.Type == "stream")
            return Results.Problem("Browsing stream queues is not supported yet.", statusCode: StatusCodes.Status400BadRequest);

        if (queue.HasLowDeliveryLimit && !request.AcceptRequeueSideEffects)
        {
            return Results.Problem(
                $"Browsing a quorum queue requeues each message, which increments its delivery count. This queue has a delivery-limit of " +
                $"{queue.DeliveryLimit}: messages that reach it are dead-lettered or dropped.",
                title: "Confirmation required",
                type: QuorumRequeueConfirmation,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["deliveryLimit"] = queue.DeliveryLimit });
        }

        var count = Math.Clamp(request.Count, 1, MaxBrowseCount);
        var truncate = options.Value.BrowseTruncateBytes;
        var raw = await client.GetMessagesAsync(request.Vhost, request.Queue, count, truncate, ct);

        var messages = raw.Select((m, i) => MessageViews.FromManagement(i, m, truncate)).ToList();
        var warnings = new List<string> { "Messages were requeued: they are now flagged as redelivered and their order may have changed." };
        if (queue.Type == "quorum")
            warnings.Add("Quorum queue: the delivery count of the browsed messages has been incremented.");

        await audit.WriteAsync(new AuditRecord(user.Username, Permissions.MessagesBrowse, AuditOutcome.Success, cluster, request.Vhost, request.Queue,
            $"requested={count} returned={messages.Count}"), ct);

        return Results.Ok(new BrowseResponse(queue.Type, queue.DeliveryLimit, queue.Messages, warnings, messages));
    }

    private static BindingDto ToDto(RmqBinding b) => new(b.Vhost, b.Source, b.Destination, b.DestinationType, b.RoutingKey, b.Arguments);

    private static SnapshotInfo Info(ClusterSnapshotService snapshots, ClusterSnapshot snapshot)
    {
        var health = snapshots.GetHealth(snapshot.Cluster);
        return new SnapshotInfo(snapshot.FetchedAt, (int)snapshot.FetchDuration.TotalMilliseconds, health.LastErrorAt, health.LastError, snapshot.Limitations);
    }

    /// <summary>Hides the existence of clusters the user has no access to at all.</summary>
    private static IResult NotFoundOrForbidden(PermissionSet permissions, string cluster) =>
        permissions.ActionsAnywhereIn(cluster).Count == 0 ? Results.NotFound() : Results.Forbid();

    private static string? Endpoint(string? queue, string? exchange, string? routingKey) =>
        queue is not null ? $"queue: {queue}"
        : exchange is not null ? $"exchange: {exchange}{(string.IsNullOrEmpty(routingKey) ? "" : $" ({routingKey})")}"
        : null;
}
