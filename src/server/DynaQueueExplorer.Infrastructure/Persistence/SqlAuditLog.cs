using DynaQueueExplorer.Core.Auditing;

namespace DynaQueueExplorer.Infrastructure.Persistence;

public sealed class SqlAuditLog(ExplorerDbContext db, TimeProvider time) : IAuditLog
{
    public async Task WriteAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        db.AuditEntries.Add(new AuditEntry
        {
            TimestampUtc = time.GetUtcNow(),
            User = record.User,
            Action = record.Action,
            Outcome = record.Outcome,
            Cluster = record.Cluster,
            Vhost = record.Vhost,
            Resource = record.Resource,
            Details = record.Details,
        });
        // The audit trail must not be lost if the caller's request is aborted mid-way.
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
