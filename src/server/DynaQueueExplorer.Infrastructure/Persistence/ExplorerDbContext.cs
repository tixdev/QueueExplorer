using Microsoft.EntityFrameworkCore;

namespace DynaQueueExplorer.Infrastructure.Persistence;

public sealed class ExplorerDbContext(DbContextOptions<ExplorerDbContext> options) : DbContext(options)
{
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RoleGrant> RoleGrants => Set<RoleGrant>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<StagingBatch> StagingBatches => Set<StagingBatch>();
    public DbSet<StagedMessage> StagedMessages => Set<StagedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("dqe");

        modelBuilder.Entity<Role>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(100);
            e.Property(r => r.Description).HasMaxLength(500);
            e.HasIndex(r => r.Name).IsUnique();
            e.HasMany(r => r.Grants).WithOne().HasForeignKey(g => g.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(r => r.Assignments).WithOne(a => a.Role).HasForeignKey(a => a.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RoleGrant>(e =>
        {
            e.Property(g => g.Action).HasMaxLength(100);
            e.Property(g => g.ClusterPattern).HasMaxLength(200);
            e.Property(g => g.VhostPattern).HasMaxLength(200);
            e.Property(g => g.QueuePattern).HasMaxLength(400);
        });

        modelBuilder.Entity<RoleAssignment>(e =>
        {
            e.Property(a => a.SubjectType).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.Subject).HasMaxLength(200);
            e.HasIndex(a => new { a.SubjectType, a.Subject, a.RoleId }).IsUnique();
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.Property(a => a.User).HasMaxLength(200);
            e.Property(a => a.Action).HasMaxLength(100);
            e.Property(a => a.Outcome).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.Cluster).HasMaxLength(200);
            e.Property(a => a.Vhost).HasMaxLength(200);
            e.Property(a => a.Resource).HasMaxLength(400);
            e.HasIndex(a => a.TimestampUtc);
            e.HasIndex(a => new { a.User, a.TimestampUtc });
        });

        modelBuilder.Entity<StagingBatch>(e =>
        {
            e.Property(b => b.Name).HasMaxLength(200);
            e.Property(b => b.CreatedBy).HasMaxLength(200);
            e.Property(b => b.Broker).HasMaxLength(30).HasDefaultValue("rabbitmq");
            e.HasIndex(b => b.Broker);
            e.Property(b => b.Origin).HasMaxLength(600);
            e.Property(b => b.OriginCluster).HasMaxLength(200);
            e.Property(b => b.OriginVhost).HasMaxLength(200);
            e.Property(b => b.OriginQueue).HasMaxLength(400);
            e.HasMany(b => b.Messages).WithOne().HasForeignKey(m => m.BatchId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StagedMessage>(e =>
        {
            e.Property(m => m.Exchange).HasMaxLength(400);
            e.Property(m => m.RoutingKey).HasMaxLength(400);
            e.Property(m => m.LastPublishedTo).HasMaxLength(800);
            e.Property(m => m.Broker).HasMaxLength(30).HasDefaultValue("rabbitmq");
            e.Property(m => m.EditedBy).HasMaxLength(200);
            e.HasIndex(m => new { m.BatchId, m.Position });
        });
    }
}
