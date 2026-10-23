using DynaQueueExplorer.Core.Auditing;

namespace DynaQueueExplorer.Infrastructure.Persistence;

public sealed class Role
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public List<RoleGrant> Grants { get; set; } = [];
    public List<RoleAssignment> Assignments { get; set; } = [];
}

/// <summary>An action granted on resources matching the cluster/vhost/queue glob patterns.</summary>
public sealed class RoleGrant
{
    public int Id { get; set; }
    public int RoleId { get; set; }
    public required string Action { get; set; }
    public string ClusterPattern { get; set; } = "*";
    public string VhostPattern { get; set; } = "*";
    public string QueuePattern { get; set; } = "*";
}

public enum SubjectType
{
    User,
    Group,
}

public sealed class RoleAssignment
{
    public int Id { get; set; }
    public int RoleId { get; set; }
    public Role? Role { get; set; }
    public SubjectType SubjectType { get; set; }
    public required string Subject { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public required string User { get; set; }
    public required string Action { get; set; }
    public AuditOutcome Outcome { get; set; }
    public string? Cluster { get; set; }
    public string? Vhost { get; set; }
    public string? Resource { get; set; }
    public string? Details { get; set; }
}

/// <summary>A set of messages parked for inspection before deciding where to publish them.</summary>
public sealed class StagingBatch
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Broker the messages come from (see BrokerTypes); a batch holds messages of a single broker.</summary>
    public string Broker { get; set; } = "rabbitmq";
    /// <summary>Where the messages came from: a file name or cluster/vhost/queue.</summary>
    public required string Origin { get; set; }
    public string? OriginCluster { get; set; }
    public string? OriginVhost { get; set; }
    public string? OriginQueue { get; set; }
    public List<StagedMessage> Messages { get; set; } = [];
}

public sealed class StagedMessage
{
    public long Id { get; set; }
    public int BatchId { get; set; }
    public int Position { get; set; }
    public string Broker { get; set; } = "rabbitmq";
    public required string Exchange { get; set; }
    public required string RoutingKey { get; set; }
    /// <summary>AMQP properties with typed header values (see AmqpValueCodec).</summary>
    public required string PropertiesJson { get; set; }
    public required byte[] Body { get; set; }
    public bool Discarded { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public string? EditedBy { get; set; }
    /// <summary>The message as it was staged, saved on the first edit so it can be restored.</summary>
    public string? OriginalPropertiesJson { get; set; }
    public byte[]? OriginalBody { get; set; }
    public DateTimeOffset? LastPublishedAt { get; set; }
    public string? LastPublishedTo { get; set; }
}
