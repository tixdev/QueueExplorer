using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using DynaQueueExplorer.RabbitMq.Management;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.RabbitMq.Snapshots;

/// <summary>
/// Keeps an in-memory snapshot of every cluster so requests never call RabbitMQ for list views.
/// <list type="bullet">
/// <item>Two tiers: the state (depths, consumers, nodes, overview, shovel status) is refreshed on every cycle, the
/// structure (vhosts, exchanges, bindings, shovel definitions) only every <c>StructureRefreshInterval</c>.</item>
/// <item>Polling happens only while someone uses the cluster; when idle it stops (or slows down, if configured), and
/// the first request afterwards waits for a fresh snapshot.</item>
/// <item>Concurrent refreshes are coalesced into one set of calls.</item>
/// </list>
/// </summary>
public sealed class ClusterSnapshotService(
    ClusterClients clients,
    IOptions<RabbitMqOptions> options,
    TimeProvider time,
    ILogger<ClusterSnapshotService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, ClusterState> _states = new(StringComparer.Ordinal);

    /// <summary>The current snapshot, if any, without marking the cluster as in use (for background samplers).</summary>
    public ClusterSnapshot? Peek(string cluster) => GetState(cluster).Snapshot;

    public ClusterHealth GetHealth(string cluster)
    {
        var state = GetState(cluster);
        return new ClusterHealth(cluster, state.Snapshot?.FetchedAt, state.LastErrorAt, state.LastError);
    }

    /// <summary>
    /// Returns the current snapshot and marks the cluster as in use, which wakes the refresh loop if it was idle.
    /// A snapshot older than twice the active interval (e.g. after idling) is refreshed before answering.
    /// </summary>
    public async Task<ClusterSnapshot> GetSnapshotAsync(string cluster, CancellationToken ct)
    {
        var state = GetState(cluster);
        var wasIdle = IsIdle(state);
        state.LastAccess = time.GetUtcNow();
        if (wasIdle)
            state.Wake();

        var snapshot = state.Snapshot;
        if (snapshot is not null && time.GetUtcNow() - snapshot.FetchedAt <= IntervalsOf(cluster).Active * 2)
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

    /// <summary>
    /// Refreshes now; callers arriving while a refresh is in flight share its result.
    /// <paramref name="includeStructure"/> also re-reads the structure regardless of its age (manual refresh).
    /// </summary>
    public async Task<ClusterSnapshot> RefreshAsync(string cluster, CancellationToken ct, bool includeStructure = false)
    {
        var state = GetState(cluster);
        Task<ClusterSnapshot>? running;
        bool sufficient;
        lock (state)
        {
            running = state.InFlight;
            sufficient = running is not null && (!includeStructure || state.InFlightIncludesStructure);
        }
        if (sufficient)
            return await running!.WaitAsync(ct);

        // A state-only refresh is running but the structure was asked for: let it finish, then start a full one.
        if (running is not null)
        {
            try
            {
                await running.WaitAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Recorded on the state; the full refresh below tries again.
            }
        }

        Task<ClusterSnapshot> refresh;
        lock (state)
        {
            if (state.InFlight is { } other)
            {
                refresh = other;
            }
            else
            {
                refresh = RefreshCoreAsync(state, includeStructure);
                // A refresh that completed synchronously (e.g. failed before its first call) has already run its
                // cleanup: registering it would leave a finished task "in flight" and block every later refresh.
                if (!refresh.IsCompleted)
                {
                    state.InFlightIncludesStructure = includeStructure;
                    state.InFlight = refresh;
                }
            }
        }
        return await refresh.WaitAsync(ct);
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

            var intervals = IntervalsOf(state.Cluster);
            var wait = !IsIdle(state) ? intervals.Active : intervals.Idle ?? Timeout.InfiniteTimeSpan;
            await state.WaitForWakeAsync(wait, time, stoppingToken);
        }
    }

    private async Task<ClusterSnapshot> RefreshCoreAsync(ClusterState state, bool forceStructure)
    {
        try
        {
            var client = clients.Get(state.Cluster);
            var started = Stopwatch.GetTimestamp();
            var structure = state.Structure;
            var readStructure = forceStructure || structure is null
                                || time.GetUtcNow() - structure.FetchedAt >= IntervalsOf(state.Cluster).Structure;

            var stateFingerprint = new PayloadFingerprint();
            var metrics = new PayloadFingerprint();
            using var cts = new CancellationTokenSource(options.Value.RequestTimeout);
            var ct = cts.Token;

            // All calls of the cycle run concurrently over the cluster's capped connection pool.
            // Node resources and shovel status need the monitoring tag: a service account without it still gets
            // everything else, and the snapshot lists what is missing.
            var overview = client.GetOverviewAsync(ct, metrics);
            var nodes = Optional(client.GetNodesAsync(ct, metrics));
            var queues = client.GetQueuesAsync(ct, stateFingerprint);
            var consumers = client.GetConsumersAsync(ct, stateFingerprint);
            var shovelStatuses = Optional(client.GetShovelStatusesAsync(ct, stateFingerprint));
            var structureTask = readStructure ? ReadStructureAsync(client, ct) : null;
            await Task.WhenAll(overview, nodes, queues, consumers, shovelStatuses, structureTask ?? Task.CompletedTask);

            if (structureTask is not null)
                state.Structure = structure = await structureTask;

            var consumersKnown = structure!.WhoAmI?.SeesEveryConsumer ?? true;
            var limitations = new List<string>(structure.Limitations);
            if (nodes.Result is null)
                limitations.Add("Node resources are not available: the service account needs the 'monitoring' tag.");
            if (shovelStatuses.Result is null)
                limitations.Add("Shovel status is not available: the service account needs the 'monitoring' tag.");
            if (!consumersKnown)
                limitations.Add("Consumer counts are unknown: without the 'monitoring' tag RabbitMQ only lists the service account's own consumers.");

            var snapshot = new ClusterSnapshot(
                state.Cluster,
                time.GetUtcNow(),
                structure!.FetchedAt,
                Stopwatch.GetElapsedTime(started),
                Combine(stateFingerprint.Value, structure.Version),
                metrics.Value,
                overview.Result,
                nodes.Result ?? [],
                structure.Vhosts,
                consumersKnown ? WithConsumerCounts(queues.Result, consumers.Result) : queues.Result.Select(q => q with { Consumers = null }).ToList(),
                structure.Exchanges,
                structure.Bindings,
                shovelStatuses.Result ?? [],
                structure.ShovelDefinitions,
                limitations)
            {
                ConsumersKnown = consumersKnown,
            };

            state.Snapshot = snapshot;
            state.LastError = null;
            logger.LogDebug("Refreshed cluster {Cluster} ({Scope}) in {Elapsed} ms ({Queues} queues, {Exchanges} exchanges, {Bindings} bindings)",
                state.Cluster, readStructure ? "state+structure" : "state", (int)snapshot.FetchDuration.TotalMilliseconds,
                snapshot.Queues.Count, snapshot.Exchanges.Count, snapshot.Bindings.Count);
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

    private async Task<Structure> ReadStructureAsync(RabbitMqManagementClient client, CancellationToken ct)
    {
        var fingerprint = new PayloadFingerprint();
        var whoAmI = Optional(client.GetWhoAmIAsync(ct));
        var vhosts = client.GetVhostsAsync(ct, fingerprint);
        var exchanges = client.GetExchangesAsync(ct, fingerprint);
        var bindings = client.GetBindingsAsync(ct, fingerprint);
        // Reading runtime parameters needs the policymaker or administrator tag.
        var shovelDefinitions = Optional(client.GetShovelDefinitionsAsync(ct, fingerprint));
        await Task.WhenAll(whoAmI, vhosts, exchanges, bindings, shovelDefinitions);

        var limitations = new List<string>();
        if (shovelDefinitions.Result is null)
            limitations.Add("Shovel definitions are not available: the service account needs the 'policymaker' or 'administrator' tag.");
        // Every vhost has at least the amq.* exchanges: a vhost without any is one the account has no permissions on.
        var withExchanges = exchanges.Result.Select(e => e.Vhost).ToHashSet(StringComparer.Ordinal);
        var unreadable = vhosts.Result.Select(v => v.Name).Where(v => !withExchanges.Contains(v)).ToList();
        if (unreadable.Count > 0)
            limitations.Add($"No permissions on {unreadable.Count} vhost(s): {string.Join(", ", unreadable.Take(5))}{(unreadable.Count > 5 ? ", …" : "")}. Their queues and exchanges are not listed.");

        return new Structure(time.GetUtcNow(), fingerprint.Value, vhosts.Result, exchanges.Result, bindings.Result,
            shovelDefinitions.Result ?? [], whoAmI.Result, limitations);
    }

    /// <summary>A call whose data the service account may not be allowed to read: access denied gives null.</summary>
    private static async Task<T?> Optional<T>(Task<T> call) where T : class
    {
        try
        {
            return await call;
        }
        catch (RabbitMqApiException e) when (e.IsAccessDenied)
        {
            return null;
        }
    }

    /// <summary>Order-dependent mix of the two tier versions, so a change in either changes the topology version.</summary>
    private static ulong Combine(ulong state, ulong structure) => state ^ (BitOperations.RotateLeft(structure, 29) * 0x9E3779B97F4A7C15UL);

    private static List<RmqQueue> WithConsumerCounts(List<RmqQueue> queues, List<RmqConsumer> consumers)
    {
        var counts = consumers
            .GroupBy(c => (c.Queue.Vhost, c.Queue.Name))
            .ToDictionary(g => g.Key, g => g.Count());
        return queues.Select(q => q with { Consumers = counts.GetValueOrDefault((q.Vhost, q.Name)) }).ToList();
    }

    /// <summary>Refresh settings of a cluster: its own overrides, falling back to the global settings.</summary>
    private (TimeSpan Active, TimeSpan Structure, TimeSpan IdleAfter, TimeSpan? Idle) IntervalsOf(string cluster)
    {
        var global = options.Value;
        var own = clients.GetOptions(cluster).Refresh;
        return (own?.ActiveRefreshInterval ?? global.ActiveRefreshInterval,
            own?.StructureRefreshInterval ?? global.StructureRefreshInterval,
            own?.IdleAfter ?? global.IdleAfter,
            own?.IdleRefreshInterval ?? global.IdleRefreshInterval);
    }

    private bool IsIdle(ClusterState state) => time.GetUtcNow() - state.LastAccess > IntervalsOf(state.Cluster).IdleAfter;

    private ClusterState GetState(string cluster) =>
        clients.Exists(cluster)
            ? _states.GetOrAdd(cluster, static name => new ClusterState(name))
            : throw new KeyNotFoundException($"Unknown cluster '{cluster}'.");

    private sealed record Structure(
        DateTimeOffset FetchedAt,
        ulong Version,
        List<RmqVhost> Vhosts,
        List<RmqExchange> Exchanges,
        List<RmqBinding> Bindings,
        List<RmqParameter> ShovelDefinitions,
        RmqWhoAmI? WhoAmI,
        IReadOnlyList<string> Limitations);

    private sealed class ClusterState(string cluster)
    {
        private readonly SemaphoreSlim _wake = new(0, 1);

        public string Cluster { get; } = cluster;
        public volatile ClusterSnapshot? Snapshot;
        public volatile Structure? Structure;
        public Task<ClusterSnapshot>? InFlight;
        public bool InFlightIncludesStructure;
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

        /// <summary>
        /// Waits for the interval (on the given clock) or a wake-up, whichever comes first. The losing wait is cancelled,
        /// so a pending semaphore wait can never swallow a later wake-up.
        /// </summary>
        public async Task WaitForWakeAsync(TimeSpan interval, TimeProvider time, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var wake = _wake.WaitAsync(cts.Token);
            var delay = Task.Delay(interval, time, cts.Token);
            await Task.WhenAny(wake, delay);
            await cts.CancelAsync();
            try
            {
                await Task.WhenAll(wake, delay);
            }
            catch (OperationCanceledException)
            {
                // Expected for the wait that lost (or on shutdown).
            }
        }
    }
}
