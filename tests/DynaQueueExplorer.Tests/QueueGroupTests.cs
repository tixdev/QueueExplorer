using DynaQueueExplorer.Core.Clusters;

namespace DynaQueueExplorer.Tests;

public class QueueGroupTests
{
    // A "T1 CH" group shaped on real names: RabbitMQ vhosts like ba-ch-dyn.t1.x.bridge, IBM MQ queues like IBCHT1.QU.…
    // (the MQ rule pins the prefix length so that CHT1 in a later name segment does not count).
    private static readonly QueueGroup T1Ch = new()
    {
        Name = "T1 CH",
        Rules =
        [
            new QueueGroupRule { Broker = "rabbitmq", Vhost = "*-ch-*.t1.*" },
            new QueueGroupRule { Broker = "ibmmq", Queue = "??CHT1.*" },
        ],
    };

    [Theory]
    [InlineData("rabbitmq", "ba-ch-dyn.t1.x.bridge", "Sales.Orders", true)]
    [InlineData("rabbitmq", "ba-ch-dx.t1.x.external-services", "error", true)]
    [InlineData("rabbitmq", "ba-ch-dyn.t3.x.bridge", "Sales.Orders", false)]
    [InlineData("rabbitmq", "ba-bs-dyn.t1.x.bridge", "Sales.Orders", false)]
    [InlineData("rabbitmq", "/", "Sales.Orders", false)]
    [InlineData("ibmmq", "RDQMI0", "IBCHT1.QU.DYNACOS.CNS.BANK.EVENTS", true)]
    [InlineData("ibmmq", "RDQMI0", "ibcht1.qu.lowercase", true)]
    [InlineData("ibmmq", "RDQMI0", "IBCHT3.QU.DYNACOS.CNS.BANK.EVENTS", false)]
    [InlineData("ibmmq", "RDQMI0", "IBBST1.QU.DYNACOS.CNS.BANK.EVENTS", false)]
    [InlineData("ibmmq", "RDQMI0", "TDV.XXCHT1.QU", false)]
    // A rule only applies to its broker: an MQ-shaped name on RabbitMQ is not in the group.
    [InlineData("rabbitmq", "ba-bs-dyn.t1.x.bridge", "IBCHT1.QU.X", false)]
    public void Rules_select_by_broker_vhost_and_queue(string broker, string vhost, string queue, bool expected) =>
        Assert.Equal(expected, T1Ch.Contains(broker, "any", vhost, queue));

    [Fact]
    public void Group_without_rules_is_empty() =>
        Assert.False(new QueueGroup { Name = "empty" }.Contains("rabbitmq", "c", "v", "q"));

    [Fact]
    public void Cluster_pattern_restricts_the_rule()
    {
        var group = new QueueGroup { Name = "g", Rules = [new QueueGroupRule { Cluster = "ittest", Queue = "error" }] };

        Assert.True(group.Contains("rabbitmq", "ittest", "/", "error"));
        Assert.False(group.Contains("rabbitmq", "prod", "/", "error"));
    }
}
