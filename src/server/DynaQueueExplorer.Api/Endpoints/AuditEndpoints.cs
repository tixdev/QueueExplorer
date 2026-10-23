using System.Text.Json;
using DynaQueueExplorer.Api.Security;
using DynaQueueExplorer.Core.Security;
using DynaQueueExplorer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DynaQueueExplorer.Api.Endpoints;

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        // Keyset pagination on Id: constant cost regardless of how large the audit table grows.
        app.MapGet("/api/audit", async (long? before, int? take, CurrentUser user, ExplorerDbContext db, CancellationToken ct) =>
        {
            var permissions = await user.GetPermissionsAsync(ct);
            if (!permissions.Allows(Permissions.AuditRead, ResourceRef.Global))
                return Results.Forbid();

            var query = db.AuditEntries.AsNoTracking();
            if (before is not null)
                query = query.Where(a => a.Id < before);

            var entries = await query
                .OrderByDescending(a => a.Id)
                .Take(Math.Clamp(take ?? 100, 1, 500))
                .ToListAsync(ct);
            // Outcomes are camelCase like every other enum in the API (the UI compares against "success").
            return Results.Ok(entries.Select(a => new AuditEntryDto(a.Id, a.TimestampUtc, a.User, a.Action,
                JsonNamingPolicy.CamelCase.ConvertName(a.Outcome.ToString()), a.Cluster, a.Vhost, a.Resource, a.Details)));
        }).RequireAuthorization();
    }
}
