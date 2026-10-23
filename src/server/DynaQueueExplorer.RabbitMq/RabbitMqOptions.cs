namespace DynaQueueExplorer.RabbitMq;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    /// <summary>
    /// How often queue depths, consumers, node resources, overview and shovel status are refreshed while someone uses
    /// the cluster. RabbitMQ recomputes its statistics every 5 s by default, so shorter intervals only add load.
    /// </summary>
    public TimeSpan ActiveRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often the structure that changes mostly with deployments (vhosts, exchanges, bindings, shovel definitions)
    /// is re-read. A manual refresh always re-reads it.
    /// </summary>
    public TimeSpan StructureRefreshInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>A cluster nobody has used for this long is idle.</summary>
    public TimeSpan IdleAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Polling interval while idle; null (default) stops polling until someone uses the cluster again, and that first
    /// request then waits for a fresh snapshot.
    /// </summary>
    public TimeSpan? IdleRefreshInterval { get; set; }

    /// <summary>
    /// How often message totals and error-queue depths are sampled for the overview trend (also while idle: two
    /// small calls, or none when a fresh snapshot exists). Null disables the trend.
    /// </summary>
    public TimeSpan? TrendSampleInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How much trend history is kept in memory.</summary>
    public TimeSpan TrendRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Upper bound of messages read per error queue by the failure analysis.</summary>
    public int ErrorAnalysisMaxMessages { get; set; } = 1000;

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
