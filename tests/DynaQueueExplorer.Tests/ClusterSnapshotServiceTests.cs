using System.Collections.Concurrent;
using System.Net;
using System.Text;
using DynaQueueExplorer.Core.Clusters;
using DynaQueueExplorer.RabbitMq;
using DynaQueueExplorer.RabbitMq.Snapshots;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DynaQueueExplorer.Tests;

/// <summary>Refresh tiers and idle behaviour, against a fake management API and a fake clock.</summary>
public sealed class ClusterSnapshotServiceTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero));
    private readonly FakeManagementApi _api = new();
    private readonly ClusterClients _clients;
    private readonly ClusterSnapshotService _service;

    public ClusterSnapshotServiceTests()
    {
        var options = Options.Create(new RabbitMqOptions
        {
            ActiveRefreshInterval = TimeSpan.FromSeconds(5),
            StructureRefreshInterval = TimeSpan.FromSeconds(60),
            IdleAfter = TimeSpan.FromMinutes(2),
            IdleRefreshInterval = null,
        });
        var clusters = Options.Create(new ClustersOptions
        {
            Items = [new ClusterOptions { Name = "c", ManagementUrl = new Uri("http://broker/"), Username = "u", Password = "p" }],
        });
        _clients = new ClusterClients(clusters, options, new FakeEnvironment(), _ => _api);
        _service = new ClusterSnapshotService(_clients, options, _time, NullLogger<ClusterSnapshotService>.Instance);
    }

    [Fact]
    public async Task Structure_is_read_less_often_than_state()
    {
        await _service.RefreshAsync("c", CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(5));
        await _service.RefreshAsync("c", CancellationToken.None);

        Assert.Equal(2, _api.Calls("api/queues"));
        Assert.Equal(2, _api.Calls("api/overview"));
        Assert.Equal(1, _api.Calls("api/bindings"));
        Assert.Equal(1, _api.Calls("api/exchanges"));

        _time.Advance(TimeSpan.FromSeconds(60));
        await _service.RefreshAsync("c", CancellationToken.None);
        Assert.Equal(2, _api.Calls("api/bindings"));
    }

    [Fact]
    public async Task Manual_refresh_rereads_the_structure()
    {
        await _service.RefreshAsync("c", CancellationToken.None);
        await _service.RefreshAsync("c", CancellationToken.None, includeStructure: true);

        Assert.Equal(2, _api.Calls("api/bindings"));
    }

    [Fact]
    public async Task Fresh_snapshots_are_served_without_calls_and_stale_ones_are_refreshed_first()
    {
        await _service.RefreshAsync("c", CancellationToken.None);
        var calls = _api.Total;

        _time.Advance(TimeSpan.FromSeconds(9));
        await _service.GetSnapshotAsync("c", CancellationToken.None);
        Assert.Equal(calls, _api.Total);

        _time.Advance(TimeSpan.FromSeconds(2));
        var snapshot = await _service.GetSnapshotAsync("c", CancellationToken.None);
        Assert.True(_api.Total > calls);
        Assert.Equal(_time.GetUtcNow(), snapshot.FetchedAt);
    }

    [Fact]
    public async Task Topology_version_changes_when_either_tier_changes()
    {
        var first = await _service.RefreshAsync("c", CancellationToken.None);
        var same = await _service.RefreshAsync("c", CancellationToken.None);
        Assert.Equal(first.TopologyVersion, same.TopologyVersion);

        _api.QueueDepth = 7;
        var depthChanged = await _service.RefreshAsync("c", CancellationToken.None);
        Assert.NotEqual(same.TopologyVersion, depthChanged.TopologyVersion);

        _api.BindingKey = "changed";
        var bindingChanged = await _service.RefreshAsync("c", CancellationToken.None, includeStructure: true);
        Assert.NotEqual(depthChanged.TopologyVersion, bindingChanged.TopologyVersion);
    }

    [Fact]
    public async Task Polls_only_while_the_cluster_is_in_use()
    {
        await _service.StartAsync(CancellationToken.None);
        await Eventually(() => _api.Calls("api/queues") == 1); // Warm-up refresh at start.

        // Nobody has used the cluster: no polling, however long we wait.
        for (var i = 0; i < 6; i++)
            await AdvanceAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _api.Calls("api/queues"));

        // Someone opens it: the stale snapshot is refreshed, then polling runs every 5 s.
        await _service.GetSnapshotAsync("c", CancellationToken.None);
        var afterAccess = _api.Calls("api/queues");
        Assert.True(afterAccess >= 2);
        await AdvanceAsync(TimeSpan.FromSeconds(5));
        await Eventually(() => _api.Calls("api/queues") > afterAccess);

        // Two minutes without requests: polling stops again.
        for (var i = 0; i < 26; i++)
            await AdvanceAsync(TimeSpan.FromSeconds(5));
        var whenIdle = _api.Calls("api/queues");
        for (var i = 0; i < 12; i++)
            await AdvanceAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(whenIdle, _api.Calls("api/queues"));

        await _service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_limited_service_account_still_gets_a_snapshot_and_its_limitations()
    {
        _api.LimitedAccount = true;

        var snapshot = await _service.RefreshAsync("c", CancellationToken.None);

        Assert.Single(snapshot.Queues);
        Assert.Empty(snapshot.Nodes);
        Assert.Empty(snapshot.ShovelStatuses);
        Assert.Empty(snapshot.ShovelDefinitions);
        Assert.False(snapshot.ConsumersKnown);
        Assert.Null(snapshot.Queues[0].Consumers);
        Assert.Contains(snapshot.Limitations, l => l.StartsWith("Node resources"));
        Assert.Contains(snapshot.Limitations, l => l.StartsWith("Shovel definitions"));
        Assert.Contains(snapshot.Limitations, l => l.StartsWith("Consumer counts"));
    }

    [Fact]
    public async Task A_full_service_account_has_no_limitations()
    {
        var snapshot = await _service.RefreshAsync("c", CancellationToken.None);

        Assert.Empty(snapshot.Limitations);
        Assert.True(snapshot.ConsumersKnown);
        Assert.Equal(1, snapshot.Queues[0].Consumers);
    }

    private async Task AdvanceAsync(TimeSpan by)
    {
        _time.Advance(by);
        // Let the refresh loop observe the fake time and run its (synchronous) fake HTTP calls.
        await Task.Delay(20);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    public void Dispose()
    {
        _service.Dispose();
        _clients.Dispose();
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    /// <summary>Answers the management API calls of a refresh with tiny payloads and counts them by path.</summary>
    private sealed class FakeManagementApi : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> _calls = new();

        public int QueueDepth { get; set; } = 3;

        /// <summary>Simulates a service account without the monitoring/policymaker tags.</summary>
        public bool LimitedAccount { get; set; }
        public string BindingKey { get; set; } = "rk";

        public int Calls(string path) => _calls.GetValueOrDefault(path);

        public int Total => _calls.Values.Sum();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            _calls.AddOrUpdate(path, 1, (_, n) => n + 1);
            if (LimitedAccount && path is "api/nodes" or "api/shovels" or "api/parameters/shovel")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"not_authorised"}""") });
            var json = path switch
            {
                "api/whoami" => LimitedAccount ? """{"name":"svc","tags":["management"]}""" : """{"name":"svc","tags":["administrator"]}""",
                "api/overview" => """{"cluster_name":"c"}""",
                "api/nodes" => """[{"name":"n1","running":true}]""",
                "api/vhosts" => """[{"name":"/"}]""",
                "api/queues" => $$"""[{"name":"q","vhost":"/","type":"quorum","messages":{{QueueDepth}}}]""",
                "api/consumers" => """[{"queue":{"name":"q","vhost":"/"}}]""",
                "api/exchanges" => """[{"name":"x","vhost":"/","type":"fanout"}]""",
                "api/bindings" => $$"""[{"source":"x","vhost":"/","destination":"q","destination_type":"queue","routing_key":"{{BindingKey}}"}]""",
                "api/shovels" or "api/parameters/shovel" => "[]",
                _ => null,
            };
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
