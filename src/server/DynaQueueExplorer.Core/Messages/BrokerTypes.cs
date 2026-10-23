namespace DynaQueueExplorer.Core.Messages;

/// <summary>Kinds of broker a message can come from; the staging area holds messages of all of them.</summary>
public static class BrokerTypes
{
    public const string RabbitMq = "rabbitmq";
    public const string IbmMq = "ibmmq";
    public const string Kafka = "kafka";

    public static readonly IReadOnlyList<string> All = [RabbitMq, IbmMq, Kafka];
}
