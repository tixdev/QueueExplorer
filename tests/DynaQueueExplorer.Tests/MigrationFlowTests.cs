using DynaQueueExplorer.Core.Flows;

namespace DynaQueueExplorer.Tests;

public class MigrationFlowTests
{
    private const string Host = "rabbit.example";
    private const string Internal = "ba-ch-dyn.t1.x.internal-services";
    private const string Bridge = "ba-ch-dyn.t1.x.bridge";

    private static readonly MigrationBridge T1 = new()
    {
        Name = "T1",
        RabbitMqCluster = "ittest",
        Vhosts = ["*.t1.x.bridge"],
        MsmqConnections = ["msmq-01", "dyn-01"],
    };

    private static RabbitFlowInput Rabbit(IReadOnlyList<FlowQueue> queues, IReadOnlyList<FlowShovel> shovels,
        IReadOnlyList<(string, string)>? exchanges = null, IReadOnlyList<FlowBinding>? bindings = null) =>
        new("ittest", Host, queues, exchanges ?? [], bindings ?? [], shovels);

    private static MsmqFlowInput Msmq(string machine, params (string Name, long Messages)[] queues) =>
        new(machine, machine, queues.Select(q => new FlowQueue(machine, $@"private$\{q.Name}", q.Messages, null)).ToList());

    private static FlowEndpoint Queue(string vhost, string name) => new(Host, vhost, "queue", name);

    [Fact]
    public void Shovel_into_a_bridged_queue_flows_to_msmq()
    {
        var rabbit = Rabbit(
            [new(Internal, "Orders.Events", 0, 1), new(Bridge, "itta.t1.ch.orders", 3, 1)],
            [new FlowShovel("orders-to-bridge", Internal, "running", Queue(Internal, "Orders.Events"), Queue(Bridge, "itta.t1.ch.orders"))]);

        var flow = Assert.Single(MigrationFlows.Build([rabbit], [Msmq("msmq-01", ("ITTA.T1.CH.Orders", 5))], [T1]));

        Assert.Equal("to-msmq", flow.Direction);
        Assert.Equal("T1", flow.Bridge);
        Assert.Equal("itta.t1.ch.orders", flow.Title);
        Assert.Equal(["Orders.Events", "itta.t1.ch.orders", @"private$\ITTA.T1.CH.Orders"], flow.Nodes.OrderBy(n => n.Stage).Select(n => n.Name));
        var bridge = Assert.Single(flow.Hops, h => h.Kind == "bridge");
        Assert.False(bridge.Inferred);
        Assert.StartsWith("rabbitmq|", bridge.From);
        Assert.StartsWith("msmq|", bridge.To);
    }

    [Fact]
    public void Shovel_out_of_a_bridged_queue_flows_to_rabbitmq()
    {
        var rabbit = Rabbit(
            [new(Bridge, "itta.t1.ch.person", 0, 1), new(Internal, "Person.Processing", 0, 1)],
            [new FlowShovel("bridge-to-person", Bridge, "running", Queue(Bridge, "itta.t1.ch.person"), Queue(Internal, "Person.Processing"))]);

        var flow = Assert.Single(MigrationFlows.Build([rabbit], [Msmq("dyn-01", ("itta.t1.ch.person", 0))], [T1]));

        Assert.Equal("to-rabbitmq", flow.Direction);
        Assert.Equal([@"private$\itta.t1.ch.person", "itta.t1.ch.person", "Person.Processing"], flow.Nodes.OrderBy(n => n.Stage).Select(n => n.Name));
    }

    [Fact]
    public void Without_shovels_the_direction_comes_from_the_name_or_stays_unknown()
    {
        var rabbit = Rabbit([new(Bridge, "itta.t1.ch.acm.torabbit.processing", 0, 1), new(Bridge, "itta.t1.ch.acm.processing", 0, 1)], []);
        var flows = MigrationFlows.Build([rabbit], [Msmq("msmq-01", ("itta.t1.ch.acm.torabbit.processing", 0), ("itta.t1.ch.acm.processing", 0))], [T1]);

        Assert.Equal("to-rabbitmq", flows.Single(f => f.Title.Contains("torabbit")).Direction);
        var unknown = flows.Single(f => !f.Title.Contains("torabbit"));
        Assert.Equal("unknown", unknown.Direction);
        Assert.True(Assert.Single(unknown.Hops).Inferred);
    }

    [Fact]
    public void Only_the_bridge_vhosts_and_machines_of_the_environment_are_paired()
    {
        // Same queue name in a T3 bridge vhost and on a T3 machine: not part of the T1 bridge.
        var rabbit = Rabbit([new(Bridge, "Particular.ServiceControl.Audit", 0, 1), new("ba-ch-dyn.t3.x.bridge", "Particular.ServiceControl.Audit", 0, 1)], []);
        var flow = Assert.Single(MigrationFlows.Build([rabbit], [Msmq("msmq-01", ("Particular.ServiceControl.Audit", 0)), Msmq("msmq-03", ("Particular.ServiceControl.Audit", 0))], [T1]));

        Assert.Equal([Bridge, "msmq-01"], flow.Nodes.Select(n => n.Vhost).Order());
    }

    [Fact]
    public void Exchanges_a_shovel_publishes_to_are_followed_through_their_bindings()
    {
        var rabbit = Rabbit(
            [new("dx", "horn.processing", 0, 1), new(Internal, "Gateway.Processing", 12, 0)],
            [new FlowShovel("dx-to-gateway", "dx", "running", Queue("dx", "horn.processing"), new FlowEndpoint(Host, Internal, "exchange", "Gateway.Processing"))],
            exchanges: [(Internal, "Gateway.Processing")],
            bindings: [new FlowBinding(Internal, "Gateway.Processing", "Gateway.Processing", "queue", "")]);

        var flow = Assert.Single(MigrationFlows.Build([rabbit], [], []));

        Assert.Equal("rabbitmq", flow.Direction);
        Assert.Equal(["shovel", "binding"], flow.Hops.Select(h => h.Kind));
        Assert.Equal([0, 1, 2], flow.Nodes.Select(n => n.Stage));
        Assert.Contains($"12 messages waiting in {Internal}/Gateway.Processing with no consumer", flow.Alerts);
    }

    [Fact]
    public void Problems_are_reported_and_flows_with_problems_come_first()
    {
        var rabbit = Rabbit(
            [new(Internal, "a", 0, 1), new(Bridge, "itta.t1.ch.a", 0, 1), new(Internal, "b", 0, 1), new(Bridge, "itta.t1.ch.b", 0, 1)],
            [
                new FlowShovel("ok", Internal, "running", Queue(Internal, "a"), Queue(Bridge, "itta.t1.ch.a")),
                new FlowShovel("broken", Internal, "terminated", Queue(Internal, "b"), Queue(Bridge, "missing.queue")),
            ]);

        var flows = MigrationFlows.Build([rabbit], [Msmq("msmq-01", ("itta.t1.ch.a.error", 4))], [T1]);

        var first = flows[0];
        Assert.Contains("Shovel broken is terminated", first.Alerts);
        Assert.Contains($"Queue {Bridge}/missing.queue does not exist", first.Alerts);
    }

    [Fact]
    public void A_shovel_to_another_host_ends_on_an_unknown_node()
    {
        var rabbit = Rabbit([new(Internal, "a", 0, 1)],
            [new FlowShovel("out", Internal, "running", Queue(Internal, "a"), new FlowEndpoint("other.example", "/", "queue", "remote"))]);

        var flow = Assert.Single(MigrationFlows.Build([rabbit], [], []));
        var remote = Assert.Single(flow.Nodes, n => n.Name == "remote");

        Assert.False(remote.Known);
        Assert.Equal("other.example", remote.Cluster);
    }
}
