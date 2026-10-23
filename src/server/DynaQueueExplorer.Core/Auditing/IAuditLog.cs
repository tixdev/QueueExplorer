namespace DynaQueueExplorer.Core.Auditing;

public enum AuditOutcome
{
    Success,
    Denied,
    Failed,
}

public sealed record AuditRecord(
    string User,
    string Action,
    AuditOutcome Outcome,
    string? Cluster = null,
    string? Vhost = null,
    string? Resource = null,
    string? Details = null);

public interface IAuditLog
{
    Task WriteAsync(AuditRecord record, CancellationToken cancellationToken);
}
