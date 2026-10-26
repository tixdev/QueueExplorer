using System.IO.Hashing;
using System.Text;
using DynaQueueExplorer.Core.Messages;
using DynaQueueExplorer.Core.Security;

namespace DynaQueueExplorer.Core.Flows;

/// <summary>
/// Where the MSMQ ⇄ RabbitMQ bridge works: a queue in one of <see cref="MigrationBridge.Vhosts"/> of the RabbitMQ
/// cluster with the same name as a queue on one of the MSMQ connections is a bridged pair.
/// </summary>
public sealed class MigrationBridgesOptions
{
    public const string SectionName = "MigrationBridges";

    public List<MigrationBridge> Items { get; set; } = [];
}

public sealed class MigrationBridge
{
    /// <summary>Usually the environment, e.g. "T1".</summary>
    public required string Name { get; set; }
    public required string RabbitMqCluster { get; set; }
    /// <summary>Glob patterns of the bridge vhosts, e.g. "*.t1.x.bridge".</summary>
    public List<string> Vhosts { get; set; } = [];
    /// <summary>MSMQ connections (machines) whose queues the bridge serves.</summary>
    public List<string> MsmqConnections { get; set; } = [];
}

// ---- Input: what the snapshots know ----------------------------------------------------------------------------

public sealed record FlowQueue(string Vhost, string Name, long? Messages, int? Consumers);

public sealed record FlowBinding(string Vhost, string Source, string Destination, string DestinationType, string RoutingKey);

/// <param name="Host">Broker host of the URI; null for the local broker (an <c>amqp://</c> URI without host).</param>
/// <param name="Kind">queue or exchange.</param>
public sealed record FlowEndpoint(string? Host, string Vhost, string Kind, string Name, string? RoutingKey = null);

public sealed record FlowShovel(string Name, string? Vhost, string State, FlowEndpoint? Source, FlowEndpoint? Destination);

/// <param name="Host">Host of the cluster's broker, to recognise shovel URIs that point at it.</param>
public sealed record RabbitFlowInput(
    string Cluster,
    string? Host,
    IReadOnlyList<FlowQueue> Queues,
    IReadOnlyList<(string Vhost, string Name)> Exchanges,
    IReadOnlyList<FlowBinding> Bindings,
    IReadOnlyList<FlowShovel> Shovels);

/// <param name="Machine">The machine label used as vhost (see MsmqAgentClients.MachineLabel).</param>
/// <param name="Queues">Private queues, named <c>private$\name</c>.</param>
public sealed record MsmqFlowInput(string Cluster, string Machine, IReadOnlyList<FlowQueue> Queues);

// ---- Output --------------------------------------------------------------------------------------------------

/// <param name="Stage">Column of the node in its flow, from the source (0) to the destination.</param>
/// <param name="Known">False for a shovel end on a broker the explorer does not know.</param>
public sealed record FlowNode(
    string Id,
    string Broker,
    string Cluster,
    string Vhost,
    string Kind,
    string Name,
    long? Messages,
    int? Consumers,
    bool Known,
    int Stage);

/// <param name="Kind">shovel, binding or bridge.</param>
/// <param name="Inferred">Bridge hops: the direction could not be derived from shovels or bindings (guessed from the name, or unknown).</param>
public sealed record FlowHop(string Kind, string From, string To, string? Name, string? State, string? Detail, bool Inferred = false);

/// <param name="Direction">to-msmq, to-rabbitmq, both, unknown (bridged, direction not derivable) or rabbitmq (shovels only).</param>
public sealed record Flow(
    string Id,
    string Title,
    string? Bridge,
    string Direction,
    IReadOnlyList<FlowNode> Nodes,
    IReadOnlyList<FlowHop> Hops,
    IReadOnlyList<string> Alerts);

/// <summary>
/// Builds the migration flows: every shovel (with the bindings of the exchange it publishes to) and every bridged
/// queue pair becomes an edge; connected nodes form one flow. Direction of a bridge hop: a bridge queue fed by a shovel
/// or binding sends to MSMQ, one drained by a shovel receives from MSMQ.
/// </summary>
public static class MigrationFlows
{
    private const int MaxBindingDepth = 2;
    private const int MaxBindingsPerExchange = 25;

    public static IReadOnlyList<Flow> Build(IReadOnlyList<RabbitFlowInput> rabbit, IReadOnlyList<MsmqFlowInput> msmq, IReadOnlyList<MigrationBridge> bridges)
    {
        var graph = new Graph();
        var clustersByHost = rabbit.Where(r => r.Host is not null).GroupBy(r => r.Host!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var r in rabbit)
        {
            var queues = r.Queues.ToDictionary(q => (q.Vhost, q.Name));
            var exchanges = r.Exchanges.ToHashSet();
            var bindingsBySource = r.Bindings.ToLookup(b => (b.Vhost, b.Source));

            foreach (var s in r.Shovels)
            {
                if (s.Source is null || s.Destination is null)
                    continue;
                var from = RabbitNode(graph, r, s.Source, clustersByHost, queues, exchanges);
                var to = RabbitNode(graph, r, s.Destination, clustersByHost, queues, exchanges);
                graph.AddHop(new FlowHop("shovel", from.Id, to.Id, s.Name, s.State, RoutingDetail(s.Source, s.Destination)));
                if (to.Kind == "exchange" && to.Cluster == r.Cluster)
                    ExpandBindings(graph, r, to, bindingsBySource, queues, exchanges, MaxBindingDepth);
            }
        }

        foreach (var bridge in bridges)
        {
            if (rabbit.FirstOrDefault(r => r.Cluster == bridge.RabbitMqCluster) is not { } r)
                continue;
            var msmqByName = msmq.Where(m => bridge.MsmqConnections.Contains(m.Cluster, StringComparer.OrdinalIgnoreCase))
                .SelectMany(m => m.Queues.Select(q => (Input: m, Queue: q)))
                .ToLookup(x => ShortMsmqName(x.Queue.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var q in r.Queues.Where(q => bridge.Vhosts.Any(p => Glob.IsMatch(p.ToLowerInvariant(), q.Vhost.ToLowerInvariant()))))
            {
                foreach (var (input, mq) in msmqByName[q.Name])
                {
                    var rabbitNode = graph.Node(Id(BrokerTypes.RabbitMq, r.Cluster, q.Vhost, "queue", q.Name),
                        id => new FlowNode(id, BrokerTypes.RabbitMq, r.Cluster, q.Vhost, "queue", q.Name, q.Messages, q.Consumers, true, 0));
                    var msmqNode = graph.Node(Id(BrokerTypes.Msmq, input.Cluster, input.Machine, "queue", mq.Name.ToLowerInvariant()),
                        id => new FlowNode(id, BrokerTypes.Msmq, input.Cluster, input.Machine, "queue", mq.Name, mq.Messages, null, true, 0));
                    graph.AddHop(new FlowHop("bridge", rabbitNode.Id, msmqNode.Id, bridge.Name, null, null), bridge: bridge.Name);
                }
            }
        }

        return graph.Flows();
    }

    /// <summary>"private$\orders" → "orders": the bridge names the RabbitMQ queue after the MSMQ queue.</summary>
    public static string ShortMsmqName(string name) =>
        name.StartsWith(@"private$\", StringComparison.OrdinalIgnoreCase) ? name[9..] : name;

    private static FlowNode RabbitNode(Graph graph, RabbitFlowInput r, FlowEndpoint e, Dictionary<string, RabbitFlowInput> clustersByHost,
        Dictionary<(string, string), FlowQueue> queues, HashSet<(string, string)> exchanges)
    {
        // A URI towards another configured cluster lands on that cluster; towards an unknown broker on an unknown node.
        var target = e.Host is null || string.Equals(e.Host, r.Host, StringComparison.OrdinalIgnoreCase) ? r : clustersByHost.GetValueOrDefault(e.Host);
        if (target is null)
            return graph.Node(Id(BrokerTypes.RabbitMq, e.Host!, e.Vhost, e.Kind, e.Name),
                id => new FlowNode(id, BrokerTypes.RabbitMq, e.Host!, e.Vhost, e.Kind, e.Name, null, null, false, 0));

        var targetQueues = target == r ? queues : target.Queues.ToDictionary(q => (q.Vhost, q.Name));
        var q = e.Kind == "queue" ? targetQueues.GetValueOrDefault((e.Vhost, e.Name)) : null;
        var known = e.Kind == "queue" ? q is not null : (target == r ? exchanges : target.Exchanges.ToHashSet()).Contains((e.Vhost, e.Name)) || e.Name.Length == 0;
        return graph.Node(Id(BrokerTypes.RabbitMq, target.Cluster, e.Vhost, e.Kind, e.Name),
            id => new FlowNode(id, BrokerTypes.RabbitMq, target.Cluster, e.Vhost, e.Kind, e.Name, q?.Messages, q?.Consumers, known, 0));
    }

    /// <summary>Where an exchange a shovel publishes to routes the messages (exchange-to-exchange bindings included, two levels).</summary>
    private static void ExpandBindings(Graph graph, RabbitFlowInput r, FlowNode exchange, ILookup<(string, string), FlowBinding> bySource,
        Dictionary<(string, string), FlowQueue> queues, HashSet<(string, string)> exchanges, int depth)
    {
        if (depth == 0)
            return;
        foreach (var b in bySource[(exchange.Vhost, exchange.Name)].Take(MaxBindingsPerExchange))
        {
            var kind = b.DestinationType == "exchange" ? "exchange" : "queue";
            var q = kind == "queue" ? queues.GetValueOrDefault((b.Vhost, b.Destination)) : null;
            var to = graph.Node(Id(BrokerTypes.RabbitMq, r.Cluster, b.Vhost, kind, b.Destination),
                id => new FlowNode(id, BrokerTypes.RabbitMq, r.Cluster, b.Vhost, kind, b.Destination, q?.Messages, q?.Consumers,
                    kind == "queue" ? q is not null : exchanges.Contains((b.Vhost, b.Destination)), 0));
            if (graph.AddHop(new FlowHop("binding", exchange.Id, to.Id, null, null, b.RoutingKey.Length > 0 ? $"key {b.RoutingKey}" : null)) && kind == "exchange")
                ExpandBindings(graph, r, to, bySource, queues, exchanges, depth - 1);
        }
    }

    private static string? RoutingDetail(FlowEndpoint from, FlowEndpoint to)
    {
        var key = to.RoutingKey ?? from.RoutingKey;
        return string.IsNullOrEmpty(key) ? null : $"key {key}";
    }

    private static string Id(string broker, string cluster, string vhost, string kind, string name) => $"{broker}|{cluster}|{vhost}|{kind}|{name}";

    private sealed class Graph
    {
        private readonly Dictionary<string, FlowNode> _nodes = new(StringComparer.Ordinal);
        private readonly List<(FlowHop Hop, string? Bridge)> _hops = [];
        private readonly HashSet<(string, string, string, string?)> _seen = [];

        public FlowNode Node(string id, Func<string, FlowNode> create) =>
            _nodes.TryGetValue(id, out var n) ? n : _nodes[id] = create(id);

        /// <summary>False when the same hop was already added (e.g. two shovels publishing to one exchange).</summary>
        public bool AddHop(FlowHop hop, string? bridge = null)
        {
            if (!_seen.Add((hop.Kind, hop.From, hop.To, hop.Name)))
                return false;
            _hops.Add((hop, bridge));
            return true;
        }

        public IReadOnlyList<Flow> Flows()
        {
            // Bridge direction from what feeds or drains the RabbitMQ end.
            var inbound = _hops.Where(h => h.Hop.Kind != "bridge").Select(h => h.Hop.To).ToHashSet();
            var outbound = _hops.Where(h => h.Hop.Kind != "bridge").Select(h => h.Hop.From).ToHashSet();
            var hops = _hops.Select(h => (Hop: h.Hop.Kind == "bridge" ? Orient(h.Hop, inbound, outbound) : h.Hop, h.Bridge)).ToList();

            // Connected components (union-find).
            var parent = _nodes.Keys.ToDictionary(k => k, k => k, StringComparer.Ordinal);
            string Find(string x)
            {
                while (parent[x] != x)
                    x = parent[x] = parent[parent[x]];
                return x;
            }
            foreach (var (hop, _) in hops)
                parent[Find(hop.From)] = Find(hop.To);

            var flows = new List<Flow>();
            foreach (var component in hops.GroupBy(h => Find(h.Hop.From)))
            {
                var flowHops = component.Select(h => h.Hop).ToList();
                var nodeIds = flowHops.SelectMany(h => new[] { h.From, h.To }).Distinct().ToList();
                var stages = Stages(nodeIds, flowHops);
                var nodes = nodeIds.Select(id => _nodes[id] with { Stage = stages[id] })
                    .OrderBy(n => n.Stage).ThenBy(n => n.Broker).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var bridgeHops = flowHops.Where(h => h.Kind == "bridge").ToList();
                var directions = bridgeHops.Select(h => h.Detail switch
                {
                    "to MSMQ" => "to-msmq",
                    "both directions" => "both",
                    "direction unknown" => "unknown",
                    _ => "to-rabbitmq",
                }).Distinct().ToList();
                var direction = directions.Count switch
                {
                    0 => "rabbitmq",
                    1 => directions[0],
                    _ => directions.Contains("to-msmq") && directions.Contains("to-rabbitmq") ? "both" : directions.First(d => d != "unknown"),
                };
                flows.Add(new Flow(FlowId(nodeIds), Title(nodes, bridgeHops), component.Select(h => h.Bridge).FirstOrDefault(b => b is not null),
                    direction, nodes, flowHops, Alerts(nodes, flowHops)));
            }
            return flows
                .OrderByDescending(f => f.Alerts.Count > 0)
                .ThenByDescending(f => f.Nodes.Sum(n => n.Messages ?? 0))
                .ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// A bridge hop is stored RabbitMQ → MSMQ. Fed by a shovel or binding, the RabbitMQ queue sends to MSMQ; drained
        /// by a shovel, it receives from MSMQ; otherwise a "torabbit" name hints at the direction.
        /// </summary>
        private FlowHop Orient(FlowHop hop, HashSet<string> inbound, HashSet<string> outbound)
        {
            var rabbit = hop.From;
            var fed = inbound.Contains(rabbit);
            var drained = outbound.Contains(rabbit);
            if (fed && !drained)
                return hop with { Detail = "to MSMQ" };
            if (drained && !fed)
                return hop with { From = hop.To, To = rabbit, Detail = "to RabbitMQ" };
            if (_nodes[rabbit].Name.Contains("torabbit", StringComparison.OrdinalIgnoreCase))
                return hop with { From = hop.To, To = rabbit, Detail = "to RabbitMQ (from the queue name)", Inferred = true };
            return hop with { Detail = fed ? "both directions" : "direction unknown", Inferred = true };
        }

        /// <summary>Longest path from a source; cycles (a shovel into its own vhost…) are cut after a few rounds.</summary>
        private static Dictionary<string, int> Stages(List<string> nodes, List<FlowHop> hops)
        {
            var stage = nodes.ToDictionary(n => n, _ => 0, StringComparer.Ordinal);
            for (var round = 0; round < nodes.Count && round < 12; round++)
            {
                var changed = false;
                foreach (var h in hops)
                {
                    if (h.From != h.To && stage[h.To] < stage[h.From] + 1)
                    {
                        stage[h.To] = stage[h.From] + 1;
                        changed = true;
                    }
                }
                if (!changed)
                    break;
            }
            return stage;
        }

        /// <summary>Named after the bridged queue when there is one, else after the first shovel's source.</summary>
        private string Title(List<FlowNode> nodes, List<FlowHop> bridgeHops)
        {
            if (bridgeHops.Count > 0)
            {
                var rabbitEnd = bridgeHops.Select(h => _nodes[h.From].Broker == BrokerTypes.RabbitMq ? _nodes[h.From] : _nodes[h.To]).First();
                return rabbitEnd.Name;
            }
            return nodes.First(n => n.Stage == 0).Name is { Length: > 0 } name ? name : "(default exchange)";
        }

        private static List<string> Alerts(List<FlowNode> nodes, List<FlowHop> hops)
        {
            var alerts = new List<string>();
            foreach (var h in hops.Where(h => h.Kind == "shovel" && h.State != "running"))
                alerts.Add($"Shovel {h.Name} is {h.State}");
            foreach (var n in nodes.Where(n => n.Kind == "queue" && (n.Messages ?? 0) > 0))
            {
                var label = n.Broker == BrokerTypes.Msmq ? $"MSMQ {n.Vhost} {ShortMsmqName(n.Name)}" : $"{n.Vhost}/{n.Name}";
                if (IsError(n.Name))
                    alerts.Add($"{n.Messages:N0} messages in error queue {label}");
                else if (n.Broker == BrokerTypes.RabbitMq && n.Consumers == 0)
                    alerts.Add($"{n.Messages:N0} messages waiting in {label} with no consumer");
            }
            foreach (var n in nodes.Where(n => !n.Known))
                alerts.Add($"{(n.Kind == "queue" ? "Queue" : "Exchange")} {n.Vhost}/{n.Name} does not exist{(n.Cluster.Contains('.') ? $" (on {n.Cluster})" : "")}");
            return alerts;
        }

        private static bool IsError(string name) =>
            name.EndsWith(".error", StringComparison.OrdinalIgnoreCase) || name.EndsWith(@"\error", StringComparison.OrdinalIgnoreCase) || name.Equals("error", StringComparison.OrdinalIgnoreCase);

        private static string FlowId(IEnumerable<string> nodeIds) =>
            Convert.ToHexString(BitConverter.GetBytes(XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(string.Join('\n', nodeIds.Order(StringComparer.Ordinal)))))).ToLowerInvariant();
    }
}
