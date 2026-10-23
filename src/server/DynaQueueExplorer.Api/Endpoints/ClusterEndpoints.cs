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
        cluster.MapGet("/shovels", GetShovels);
        cluster.MapGet("/queue", GetQueue);
        cluster.MapPost("/refresh", Refresh);
        cluster.MapPost("/messages/browse", Browse);
    }

    private static async Task<IResult> GetOverview(
        string cluster, HttpContext http, CurrentUser user, ClusterSnapshotService snapshots, ConditionalJson conditional, CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        var canOverview = permissions.Allows(Permissions.OverviewRead, new ResourceRef(cluster));
        var canNodes = permissions.Allows(Permissions.NodesRead, new ResourceRef(cluster));
        if (!canOverview && !canNodes)
            return NotFoundOrForbidden(permissions, cluster);

        var snapshot = await snapshots.GetSnapshotAsync(cluster, ct);
        return conditional.Respond(http, "overview", cluster, snapshot.MetricsVersion, await user.GetPermissionFingerprintAsync(ct), () =>
            new OverviewResponse(Info(snapshots, snapshot), canOverview ? snapshot.Overview : null, canNodes ? snapshot.Nodes : null));
    }

    private static async Task<IResult> GetTopology(
        string cluster, HttpContext http, CurrentUser user, ClusterSnapshotService snapshots, ConditionalJson conditional, CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        if (!permissions.AllowsAnywhere(Permissions.QueuesRead, cluster) && !permissions.AllowsAnywhere(Permissions.ExchangesRead, cluster))
            return NotFoundOrForbidden(permissions, cluster);

        var snapshot = await snapshots.GetSnapshotAsync(cluster, ct);
        return conditional.Respond(http, "topology", cluster, snapshot.TopologyVersion, await user.GetPermissionFingerprintAsync(ct),
            () => BuildTopology(snapshots, snapshot, permissions));
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

            var definitions = snapshot.ShovelDefinitions.ToDictionary(d => (d.Vhost, d.Name), d => d.Value);
            var statuses = snapshot.ShovelStatuses.ToDictionary(s => (s.Vhost ?? string.Empty, s.Name));

            var keys = statuses.Keys.Union(definitions.Keys.Select(k => (k.Vhost, k.Name)));
            return keys
                .Where(k => CanRead(k.Item1))
                .Select(k =>
                {
                    statuses.TryGetValue(k, out var status);
                    JsonElement? definition = definitions.TryGetValue(k, out var d) ? CredentialMasking.MaskJson(d) : null;
                    return new ShovelDto(
                        k.Item2,
                        k.Item1.Length > 0 ? k.Item1 : null,
                        status?.Type ?? "dynamic",
                        status?.State ?? "not running",
                        status?.Node,
                        status?.Timestamp,
                        // A shovel that cannot connect has no status: fall back to its definition.
                        Endpoint(status?.SrcQueue ?? Field(definition, "src-queue"), status?.SrcExchange ?? Field(definition, "src-exchange"),
                            status?.SrcExchangeKey ?? Field(definition, "src-exchange-key")),
                        Endpoint(status?.DestQueue ?? Field(definition, "dest-queue"), status?.DestExchange ?? Field(definition, "dest-exchange"),
                            status?.DestExchangeKey ?? Field(definition, "dest-exchange-key")),
                        CredentialMasking.MaskUri(status?.SrcUri) ?? Field(definition, "src-uri"),
                        CredentialMasking.MaskUri(status?.DestUri) ?? Field(definition, "dest-uri"),
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

    private static async Task<IResult> Refresh(string cluster, CurrentUser user, ClusterSnapshotService snapshots, CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        if (permissions.ActionsAnywhereIn(cluster).Count == 0)
            return NotFoundOrForbidden(permissions, cluster);

        // A manual refresh also re-reads the structure (exchanges, bindings…), which is otherwise read less often.
        var snapshot = await snapshots.RefreshAsync(cluster, ct, includeStructure: true);
        return Results.Ok(Info(snapshots, snapshot));
    }

    private static async Task<IResult> Browse(
        string cluster,
        BrowseRequest request,
        CurrentUser user,
        ClusterClients clients,
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
        return new SnapshotInfo(snapshot.FetchedAt, (int)snapshot.FetchDuration.TotalMilliseconds, health.LastErrorAt, health.LastError);
    }

    /// <summary>Hides the existence of clusters the user has no access to at all.</summary>
    private static IResult NotFoundOrForbidden(PermissionSet permissions, string cluster) =>
        permissions.ActionsAnywhereIn(cluster).Count == 0 ? Results.NotFound() : Results.Forbid();

    /// <summary>A string field of a (masked) shovel definition; URI fields may also be arrays, of which the first is used.</summary>
    private static string? Field(JsonElement? definition, string name)
    {
        if (definition is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Array when value.GetArrayLength() > 0 => value[0].GetString(),
            _ => null,
        };
    }

    private static string? Endpoint(string? queue, string? exchange, string? routingKey) =>
        queue is not null ? $"queue: {queue}"
        : exchange is not null ? $"exchange: {exchange}{(string.IsNullOrEmpty(routingKey) ? "" : $" ({routingKey})")}"
        : null;
}
