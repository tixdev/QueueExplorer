using DynaQueueExplorer.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace DynaQueueExplorer.Infrastructure.Persistence;

/// <summary>Creates the default roles for the test phase when the database has none.</summary>
public static class DevelopmentSeeder
{
    public static async Task SeedAsync(ExplorerDbContext db, CancellationToken ct)
    {
        if (await db.Roles.AnyAsync(ct))
            return;

        db.Roles.AddRange(
            new Role
            {
                Name = "Administrators",
                Description = "Every action on every cluster.",
                Grants = [new RoleGrant { Action = Permissions.Any }],
                Assignments = [new RoleAssignment { SubjectType = SubjectType.Group, Subject = "admins" }],
            },
            new Role
            {
                Name = "Support",
                Description = "Read everything; browse, export, move and republish messages in the sales and billing vhosts; staging.",
                Grants =
                [
                    new RoleGrant { Action = Permissions.OverviewRead },
                    new RoleGrant { Action = Permissions.NodesRead },
                    new RoleGrant { Action = Permissions.QueuesRead },
                    new RoleGrant { Action = Permissions.ExchangesRead },
                    new RoleGrant { Action = Permissions.ShovelsRead },
                    new RoleGrant { Action = Permissions.MessagesBrowse, VhostPattern = "sales" },
                    new RoleGrant { Action = Permissions.MessagesBrowse, VhostPattern = "billing" },
                    new RoleGrant { Action = Permissions.MessagesExport, VhostPattern = "sales" },
                    new RoleGrant { Action = Permissions.MessagesExport, VhostPattern = "billing" },
                    new RoleGrant { Action = Permissions.MessagesMove, VhostPattern = "sales" },
                    new RoleGrant { Action = Permissions.MessagesMove, VhostPattern = "billing" },
                    new RoleGrant { Action = Permissions.MessagesPublish, VhostPattern = "sales" },
                    new RoleGrant { Action = Permissions.MessagesPublish, VhostPattern = "billing" },
                    new RoleGrant { Action = Permissions.MessagesDelete, VhostPattern = "sales" },
                    new RoleGrant { Action = Permissions.MessagesDelete, VhostPattern = "billing" },
                    new RoleGrant { Action = Permissions.StagingRead },
                    new RoleGrant { Action = Permissions.StagingWrite },
                ],
                Assignments = [new RoleAssignment { SubjectType = SubjectType.Group, Subject = "support" }],
            },
            new Role
            {
                Name = "Sales team",
                Description = "The sales vhost only: its endpoint queues and the error queue, no delay infrastructure.",
                Grants =
                [
                    new RoleGrant { Action = Permissions.QueuesRead, VhostPattern = "sales", QueuePattern = "Sales.*" },
                    new RoleGrant { Action = Permissions.QueuesRead, VhostPattern = "sales", QueuePattern = "error" },
                    new RoleGrant { Action = Permissions.ExchangesRead, VhostPattern = "sales" },
                    new RoleGrant { Action = Permissions.MessagesBrowse, VhostPattern = "sales", QueuePattern = "error" },
                ],
                Assignments = [new RoleAssignment { SubjectType = SubjectType.User, Subject = "sales" }],
            });

        await db.SaveChangesAsync(ct);
    }
}
