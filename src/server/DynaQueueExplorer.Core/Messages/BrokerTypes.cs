namespace DynaQueueExplorer.Core.Messages;

/// <summary>Kinds of broker a message can come from; the staging area holds messages of all of them.</summary>
public static class BrokerTypes
{
    public const string RabbitMq = "rabbitmq";
    public const string IbmMq = "ibmmq";
    public const string Msmq = "msmq";
    public const string Kafka = "kafka";

    public static readonly IReadOnlyList<string> All = [RabbitMq, IbmMq, Msmq, Kafka];

    public static string Label(string broker) => broker switch
    {
        RabbitMq => "RabbitMQ",
        IbmMq => "IBM MQ",
        Msmq => "MSMQ",
        Kafka => "Kafka",
        _ => broker,
    };
}
