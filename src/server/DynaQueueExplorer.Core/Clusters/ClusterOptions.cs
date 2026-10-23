namespace DynaQueueExplorer.Core.Clusters;

public sealed class ClustersOptions
{
    public const string SectionName = "Clusters";

    public List<ClusterOptions> Items { get; set; } = [];
}

/// <summary>
/// A RabbitMQ cluster reached through its management API with a privileged service account.
/// The credentials never leave the server.
/// </summary>
public sealed class ClusterOptions
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required Uri ManagementUrl { get; set; }

    /// <summary>AMQP endpoint used to move, copy, export and publish messages (e.g. amqp://host:5672 or amqps://…).</summary>
    public Uri? AmqpUrl { get; set; }
    /// <summary>
    /// PEM/DER file of the certificate authority that issued the broker's TLS certificates, for brokers whose CA is not
    /// in the machine's trust store (e.g. an internal CA). Relative paths are resolved against the application folder.
    /// The server certificate is still fully validated (chain and host name) against this CA.
    /// </summary>
    public string? CaCertificatePath { get; set; }

    public required string Username { get; set; }
    public required string Password { get; set; }

    /// <summary>Per-cluster overrides of the refresh settings (e.g. slower polling for production).</summary>
    public RefreshOverrides? Refresh { get; set; }
}

/// <summary>Unset values fall back to the global <c>RabbitMq</c> settings.</summary>
public sealed class RefreshOverrides
{
    public TimeSpan? ActiveRefreshInterval { get; set; }
    public TimeSpan? StructureRefreshInterval { get; set; }
    public TimeSpan? IdleAfter { get; set; }
    public TimeSpan? IdleRefreshInterval { get; set; }
}
