namespace DynaQueueExplorer.Core.Clusters;

public sealed class ClustersOptions
{
    public const string SectionName = "Clusters";

    public List<ClusterOptions> Items { get; set; } = [];
}

/// <summary>
/// A connection to a broker: a RabbitMQ cluster (management API + AMQP) or an IBM MQ queue manager (client channel).
/// It uses a service account whose credentials never leave the server.
/// </summary>
public sealed class ClusterOptions
{
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary><c>rabbitmq</c> (default), <c>ibmmq</c> or <c>msmq</c>; see <c>BrokerTypes</c>.</summary>
    public string Broker { get; set; } = "rabbitmq";

    // ---- RabbitMQ ----
    public Uri? ManagementUrl { get; set; }

    /// <summary>AMQP endpoint used to move, copy, export and publish messages (e.g. amqp://host:5672 or amqps://…).</summary>
    public Uri? AmqpUrl { get; set; }
    /// <summary>
    /// PEM/DER file of the certificate authority that issued the broker's TLS certificates, for brokers whose CA is not
    /// in the machine's trust store (e.g. an internal CA). Relative paths are resolved against the application folder.
    /// The server certificate is still fully validated (chain and host name) against this CA.
    /// </summary>
    public string? CaCertificatePath { get; set; }

    /// <summary>Service account of RabbitMQ and IBM MQ; unused for MSMQ (the agent's Windows account reads the queues).</summary>
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    // ---- IBM MQ ----
    public string? QueueManager { get; set; }
    /// <summary>IBM MQ: host of the listener. MSMQ: the machine, as in MSMQ format names (e.g. msmq-01.example.com).</summary>
    public string? Host { get; set; }
    public int Port { get; set; } = 1414;
    /// <summary>Server-connection channel, e.g. APP.SVRCONN.</summary>
    public string? Channel { get; set; }
    /// <summary>TLS CipherSpec of the channel (e.g. ECDHE_RSA_AES_256_GCM_SHA384); empty for a plain channel.</summary>
    public string? CipherSpec { get; set; }
    /// <summary>PKCS#12 file with the client certificate, for channels that require mutual TLS.</summary>
    public string? ClientCertificatePath { get; set; }
    public string? ClientCertificatePassword { get; set; }
    /// <summary>
    /// Queues to show when the account may not run PCF commands (no access to the command queue): their depth is then
    /// read one by one with MQINQ. Ignored when PCF works.
    /// </summary>
    public List<string> Queues { get; set; } = [];
    /// <summary>
    /// Time zone of the queue manager (IANA id, e.g. Europe/Rome; default UTC). IBM MQ reports last put/get times in
    /// the queue manager's local time without saying which one it is.
    /// </summary>
    public string? TimeZone { get; set; }

    // ---- MSMQ ----
    /// <summary>The MSMQ agent that reads <see cref="Host"/> (e.g. https://msmq-agent.example.com:5180); one agent can serve several machines.</summary>
    public Uri? AgentUrl { get; set; }
    /// <summary>Shared secret of the agent (its Agent:ApiKey).</summary>
    public string? AgentApiKey { get; set; }

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
