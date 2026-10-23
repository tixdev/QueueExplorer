using DynaQueueExplorer.Api.Security;
using DynaQueueExplorer.Core.Auditing;
using DynaQueueExplorer.Core.Messages;
using DynaQueueExplorer.Core.Security;
using DynaQueueExplorer.RabbitMq.Snapshots;

namespace DynaQueueExplorer.Api.Endpoints;

public sealed record TrendResponse(int IntervalSeconds, IReadOnlyList<TrendSample> Samples);

/// <param name="Analysis">Last failure analysis, if the user may browse the queue and it has been analysed.</param>
public sealed record ErrorQueueSummary(string Vhost, string Queue, long Messages, bool CanAnalyze, QueueFailureAnalysis? Analysis);

public sealed record AnalyzeRequest(string? Vhost = null, string? Queue = null);

/// <summary>Data for the overview page beyond the snapshot: message trend and NServiceBus failure summary.</summary>
public static class OverviewEndpoints
{
    private static readonly TimeSpan MaxWindow = TimeSpan.FromHours(24);

    public static void MapOverviewEndpoints(this IEndpointRouteBuilder app)
    {
        var cluster = app.MapGroup("/api/clusters/{cluster}").RequireAuthorization();
        cluster.MapGet("/trend", Trend);
        cluster.MapGet("/errors", Errors);
        cluster.MapPost("/errors/analyze", Analyze);
    }

    private static async Task<IResult> Trend(string cluster, int? minutes, CurrentUser user, ClusterTrends trends, CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        if (!permissions.Allows(Permissions.OverviewRead, new ResourceRef(cluster)))
            return permissions.ActionsAnywhereIn(cluster).Count == 0 ? Results.NotFound() : Results.Forbid();

        var window = TimeSpan.FromMinutes(Math.Clamp(minutes ?? 60, 5, (int)MaxWindow.TotalMinutes));
        return Results.Ok(new TrendResponse((int)(trends.Interval?.TotalSeconds ?? 0), trends.Get(cluster, window)));
    }

    private static async Task<IResult> Errors(string cluster, CurrentUser user, ClusterSnapshotService snapshots, FailureAnalysis analysis, CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        if (!permissions.AllowsAnywhere(Permissions.QueuesRead, cluster))
            return permissions.ActionsAnywhereIn(cluster).Count == 0 ? Results.NotFound() : Results.Forbid();

        var snapshot = await snapshots.GetSnapshotAsync(cluster, ct);
        return Results.Ok(ErrorQueues(cluster, snapshot, permissions, analysis));
    }

    /// <summary>Analyses one error queue, or every visible error queue with messages the user may browse.</summary>
    private static async Task<IResult> Analyze(
        string cluster, AnalyzeRequest request, CurrentUser user, ClusterSnapshotService snapshots, FailureAnalysis analysis,
        IAuditLog audit, TimeProvider time, CancellationToken ct)
    {
        var permissions = await user.GetPermissionsAsync(ct);
        var snapshot = await snapshots.GetSnapshotAsync(cluster, ct);
        var targets = snapshot.Queues
            .Where(q => QueueNaming.IsErrorQueue(q.Name) && (q.Messages ?? 0) > 0)
            .Where(q => request.Vhost is null || (q.Vhost == request.Vhost && (request.Queue is null || q.Name == request.Queue)))
            .Where(q => permissions.Allows(Permissions.MessagesBrowse, new ResourceRef(cluster, q.Vhost, q.Name)))
            .ToList();
        if (targets.Count == 0)
            return Results.Ok(ErrorQueues(cluster, snapshot, permissions, analysis));

        // One queue at a time: an on-demand analysis must not burst the broker.
        foreach (var q in targets)
        {
            var result = await analysis.AnalyzeAsync(cluster, q.Vhost, q.Name, user.Username, time, ct);
            await audit.WriteAsync(new AuditRecord(user.Username, Permissions.MessagesBrowse, AuditOutcome.Success, cluster, q.Vhost, q.Name,
                result.Skipped is null ? $"failure analysis read={result.Analyzed}" : "failure analysis skipped (low delivery limit)"), ct);
        }
        return Results.Ok(ErrorQueues(cluster, snapshot, permissions, analysis));
    }

    private static List<ErrorQueueSummary> ErrorQueues(string cluster, ClusterSnapshot snapshot, PermissionSet permissions, FailureAnalysis analysis) =>
        snapshot.Queues
            .Where(q => QueueNaming.IsErrorQueue(q.Name) && permissions.Allows(Permissions.QueuesRead, new ResourceRef(cluster, q.Vhost, q.Name)))
            .Select(q =>
            {
                var canAnalyze = permissions.Allows(Permissions.MessagesBrowse, new ResourceRef(cluster, q.Vhost, q.Name));
                return new ErrorQueueSummary(q.Vhost, q.Name, q.Messages ?? 0, canAnalyze, canAnalyze ? analysis.Get(cluster, q.Vhost, q.Name) : null);
            })
            .OrderByDescending(q => q.Messages)
            .ThenBy(q => q.Vhost, StringComparer.Ordinal)
            .ToList();
}
