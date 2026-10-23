using System.Collections.Concurrent;
using System.Diagnostics;
using DynaQueueExplorer.RabbitMq.Management;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.RabbitMq.Snapshots;

/// <summary>
/// Keeps a warm in-memory snapshot of every cluster. Requests read the snapshot instead of calling
/// RabbitMQ, concurrent refreshes are coalesced, and polling slows down when a cluster is idle.
/// </summary>
public sealed class ClusterSnapshotService(
    ClusterClients clients,
    IOptions<RabbitMqOptions> options,
    TimeProvider time,
    ILogger<ClusterSnapshotService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, ClusterState> _states = new(StringComparer.Ordinal);

    public ClusterHealth GetHealth(string cluster)
    {
        var state = GetState(cluster);
        return new ClusterHealth(cluster, state.Snapshot?.FetchedAt, state.LastErrorAt, state.LastError);
    }

    /// <summary>
    /// Returns the current snapshot. Marks the cluster as active, which wakes the refresh loop if it was idling;
    /// a snapshot older than twice the active interval (e.g. after idling) is refreshed before answering.
    /// </summary>
    public async Task<ClusterSnapshot> GetSnapshotAsync(string cluster, CancellationToken ct)
    {
        var state = GetState(cluster);
        var wasIdle = IsIdle(state);
        state.LastAccess = time.GetUtcNow();
        if (wasIdle)
            state.Wake();

        var snapshot = state.Snapshot;
        if (snapshot is not null && time.GetUtcNow() - snapshot.FetchedAt <= options.Value.ActiveRefreshInterval * 2)
            return snapshot;

        try
        {
            return await RefreshAsync(cluster, ct);
        }
        catch (Exception) when (snapshot is not null && !ct.IsCancellationRequested)
        {
            // The cluster is failing: a stale snapshot (flagged through GetHealth) beats no answer.
            return snapshot;
        }
    }

    /// <summary>Refreshes now; callers arriving while a refresh is in flight share its result.</summary>
    public Task<ClusterSnapshot> RefreshAsync(string cluster, CancellationToken ct)
    {
        var state = GetState(cluster);
        Task<ClusterSnapshot> refresh;
        lock (state)
        {
            refresh = state.InFlight ??= RefreshCoreAsync(state);
        }
        return refresh.WaitAsync(ct);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(clients.Clusters.Select(c => RunLoopAsync(GetState(c.Name), stoppingToken)));

    private async Task RunLoopAsync(ClusterState state, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(state.Cluster, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Already recorded on the state; keep serving the last good snapshot.
            }

            var interval = IsIdle(state) ? options.Value.IdleRefreshInterval : options.Value.ActiveRefreshInterval;
            await state.WaitForWakeAsync(interval, stoppingToken);
        }
    }

    private async Task<ClusterSnapshot> RefreshCoreAsync(ClusterState state)
    {
        try
        {
            var client = clients.Get(state.Cluster);
            var started = Stopwatch.GetTimestamp();
            var topology = new PayloadFingerprint();
            var metrics = new PayloadFingerprint();
            using var cts = new CancellationTokenSource(options.Value.RequestTimeout);
            var ct = cts.Token;

            // All bulk calls run concurrently over the cluster's capped connection pool.
            var overview = client.GetOverviewAsync(ct, metrics);
            var nodes = client.GetNodesAsync(ct, metrics);
            var vhosts = client.GetVhostsAsync(ct, topology);
            var queues = client.GetQueuesAsync(ct, topology);
            var consumers = client.GetConsumersAsync(ct, topology);
            var exchanges = client.GetExchangesAsync(ct, topology);
            var bindings = client.GetBindingsAsync(ct, topology);
            var shovelStatuses = client.GetShovelStatusesAsync(ct, topology);
            var shovelDefinitions = client.GetShovelDefinitionsAsync(ct, topology);
            await Task.WhenAll(overview, nodes, vhosts, queues, consumers, exchanges, bindings, shovelStatuses, shovelDefinitions);

            var snapshot = new ClusterSnapshot(
                state.Cluster,
                time.GetUtcNow(),
                Stopwatch.GetElapsedTime(started),
                topology.Value,
                metrics.Value,
                overview.Result,
                nodes.Result,
                vhosts.Result,
                WithConsumerCounts(queues.Result, consumers.Result),
                exchanges.Result,
                bindings.Result,
                shovelStatuses.Result,
                shovelDefinitions.Result);

            state.Snapshot = snapshot;
            state.LastError = null;
            logger.LogDebug("Refreshed cluster {Cluster} in {Elapsed} ms ({Queues} queues, {Exchanges} exchanges, {Bindings} bindings)",
                state.Cluster, (int)snapshot.FetchDuration.TotalMilliseconds, snapshot.Queues.Count, snapshot.Exchanges.Count, snapshot.Bindings.Count);
            return snapshot;
        }
        catch (Exception e)
        {
            state.LastError = e.Message;
            state.LastErrorAt = time.GetUtcNow();
            logger.LogWarning(e, "Refresh of cluster {Cluster} failed", state.Cluster);
            throw;
        }
        finally
        {
            lock (state)
            {
                state.InFlight = null;
            }
        }
    }

    private static List<RmqQueue> WithConsumerCounts(List<RmqQueue> queues, List<RmqConsumer> consumers)
    {
        var counts = consumers
            .GroupBy(c => (c.Queue.Vhost, c.Queue.Name))
            .ToDictionary(g => g.Key, g => g.Count());
        return queues.Select(q => q with { Consumers = counts.GetValueOrDefault((q.Vhost, q.Name)) }).ToList();
    }

    private bool IsIdle(ClusterState state) => time.GetUtcNow() - state.LastAccess > options.Value.IdleAfter;

    private ClusterState GetState(string cluster) =>
        clients.Exists(cluster)
            ? _states.GetOrAdd(cluster, static name => new ClusterState(name))
            : throw new KeyNotFoundException($"Unknown cluster '{cluster}'.");

    private sealed class ClusterState(string cluster)
    {
        private readonly SemaphoreSlim _wake = new(0, 1);

        public string Cluster { get; } = cluster;
        public volatile ClusterSnapshot? Snapshot;
        public Task<ClusterSnapshot>? InFlight;
        public DateTimeOffset LastAccess = DateTimeOffset.MinValue;
        public string? LastError;
        public DateTimeOffset? LastErrorAt;

        public void Wake()
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already signalled.
            }
        }

        public Task WaitForWakeAsync(TimeSpan interval, CancellationToken ct) =>
            _wake.WaitAsync(interval, ct).ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
