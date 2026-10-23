namespace DynaQueueExplorer.RabbitMq;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>Snapshot refresh interval while someone has used the cluster recently.</summary>
    public TimeSpan ActiveRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Snapshot refresh interval when nobody has used the cluster for <see cref="IdleAfter"/>.</summary>
    public TimeSpan IdleRefreshInterval { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan IdleAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Upper bound of TCP connections towards each cluster's management API.</summary>
    public int MaxConnectionsPerCluster { get; set; } = 4;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>AMQP connections (one per cluster and vhost) are closed after this long without use.</summary>
    public TimeSpan AmqpIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound of messages a single move/copy/export operation reads from a queue.</summary>
    public int MaxMessagesPerOperation { get; set; } = 10_000;

    /// <summary>Payloads larger than this are truncated by RabbitMQ when browsing messages.</summary>
    public int BrowseTruncateBytes { get; set; } = 256 * 1024;
}
