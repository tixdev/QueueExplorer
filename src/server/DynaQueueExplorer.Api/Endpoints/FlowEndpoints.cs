using System.IO.Hashing;
using System.Text;
using DynaQueueExplorer.Api.Http;
using DynaQueueExplorer.Api.Security;
using DynaQueueExplorer.Core.Flows;
using DynaQueueExplorer.Core.Msmq;
using DynaQueueExplorer.Core.Security;
using DynaQueueExplorer.Msmq;
using DynaQueueExplorer.RabbitMq.Snapshots;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.Api.Endpoints;

/// <param name="Pending">Connections whose snapshot is not there yet (MSMQ machines being read): their flows are missing.</param>
public sealed record FlowsResponse(DateTimeOffset GeneratedAt, IReadOnlyList<string> Bridges, IReadOnlyList<Flow> Flows, IReadOnlyList<string> Pending);

/// <summary>
/// Migration flows across brokers: RabbitMQ shovels, the bindings of the exchanges they publish to, and the MSMQ ⇄
/// RabbitMQ bridge (a queue in a bridge vhost named like an MSMQ queue). Built from the snapshots, never from the brokers.
/// </summary>
public static class FlowEndpoints
{
    public static void MapFlowEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/flows", GetFlows).RequireAuthorization();

    private static async Task<IResult> GetFlows(
        HttpContext http, CurrentUser user, ClusterClients rabbitClusters, ClusterSnapshotService snapshots, MsmqAgentClients agents,
        MsmqSnapshotService msmq, IOptions<MigrationBridgesOptions> bridges, ConditionalJson conditional, TimeProvider time, CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        var versions = new StringBuilder();
        var pending = new List<string>();

        var rabbit = new List<RabbitFlowInput>();
        var canShovels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in rabbitClusters.Clusters.Where(c => permissions.ActionsAnywhereIn(c.Name).Count > 0))
        {
            ClusterSnapshot snapshot;
            try
            {
                snapshot = await snapshots.GetSnapshotAsync(c.Name, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                pending.Add(c.Name);
                continue;
            }
            versions.Append(c.Name).Append(snapshot.TopologyVersion).Append(snapshot.MetricsVersion);
            if (permissions.AllowsAnywhere(Permissions.ShovelsRead, c.Name))
                canShovels.Add(c.Name);
            rabbit.Add(new RabbitFlowInput(
                c.Name,
                c.AmqpUrl?.Host ?? c.ManagementUrl?.Host,
                snapshot.Queues.Select(q => new FlowQueue(q.Vhost, q.Name, q.Messages, q.Consumers)).ToList(),
                snapshot.Exchanges.Select(e => (e.Vhost, e.Name)).ToList(),
                snapshot.Bindings.Where(b => b.Source.Length > 0).Select(b => new FlowBinding(b.Vhost, b.Source, b.Destination, b.DestinationType, b.RoutingKey)).ToList(),
                canShovels.Contains(c.Name) ? ShovelViews.Merge(snapshot).Select(ShovelViews.ToFlow).ToList() : []));
        }

        var machines = new List<MsmqFlowInput>();
        foreach (var m in agents.Machines.Where(m => permissions.ActionsAnywhereIn(m.Name).Count > 0))
        {
            if (msmq.Peek(m.Name) is not { } snapshot)
            {
                pending.Add(m.Name);
                continue;
            }
            versions.Append(m.Name).Append(snapshot.Version);
            machines.Add(new MsmqFlowInput(m.Name, snapshot.Machine,
                snapshot.Queues.Where(q => q.Kind == MsmqQueueKinds.Private).Select(q => new FlowQueue(snapshot.Machine, q.Name, q.Messages, null)).ToList()));
        }
        versions.Append(string.Join(',', pending));

        var version = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(versions.ToString()));
        return conditional.Respond(http, "flows", "*", version, await user.GetPermissionFingerprintAsync(ct), () =>
        {
            var known = rabbitClusters.Clusters.Select(c => c.Name).Concat(agents.Machines.Select(m => m.Name)).ToHashSet(StringComparer.Ordinal);
            var flows = MigrationFlows.Build(rabbit, machines, bridges.Value.Items)
                .Select(f => Visible(f, permissions, known))
                .OfType<Flow>()
                .ToList();
            return new FlowsResponse(time.GetUtcNow(), bridges.Value.Items.Select(b => b.Name).ToList(), flows, pending);
        });
    }

    /// <summary>
    /// The part of a flow the user may see: queues need queues.read, exchanges exchanges.read; hops whose ends are hidden
    /// disappear, and so does a flow left without hops. Shovel ends on brokers the explorer does not know stay: they
    /// come from shovel definitions, which are only included for users allowed to read shovels.
    /// </summary>
    private static Flow? Visible(Flow flow, PermissionSet permissions, IReadOnlySet<string> knownClusters)
    {
        bool CanSee(FlowNode n) =>
            !knownClusters.Contains(n.Cluster) || (n.Kind == "queue"
                ? permissions.Allows(Permissions.QueuesRead, new ResourceRef(n.Cluster, n.Vhost, n.Name))
                : permissions.Allows(Permissions.ExchangesRead, new ResourceRef(n.Cluster, n.Vhost)));

        var nodes = flow.Nodes.Where(CanSee).ToList();
        if (nodes.Count == flow.Nodes.Count)
            return flow;
        var ids = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var hops = flow.Hops.Where(h => ids.Contains(h.From) && ids.Contains(h.To)).ToList();
        if (hops.Count == 0)
            return null;
        var used = hops.SelectMany(h => new[] { h.From, h.To }).ToHashSet(StringComparer.Ordinal);
        return flow with { Nodes = nodes.Where(n => used.Contains(n.Id)).ToList(), Hops = hops };
    }
}
