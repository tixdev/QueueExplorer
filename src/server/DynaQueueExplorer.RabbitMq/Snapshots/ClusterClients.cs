using System.Net;
using DynaQueueExplorer.Core.Clusters;
using DynaQueueExplorer.RabbitMq.Management;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.RabbitMq.Snapshots;

/// <summary>
/// One long-lived, connection-pooled <see cref="HttpClient"/> per cluster, shared by every request
/// and by the background refresh. The pool is capped, so load on RabbitMQ does not grow with users.
/// </summary>
public sealed class ClusterClients : IDisposable
{
    private readonly Dictionary<string, (HttpClient Http, RabbitMqManagementClient Client, ClusterOptions Options)> _clusters;

    public ClusterClients(IOptions<ClustersOptions> clusters, IOptions<RabbitMqOptions> options, IHostEnvironment environment)
    {
        _clusters = clusters.Value.Items.ToDictionary(c => c.Name, c =>
        {
            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = options.Value.MaxConnectionsPerCluster,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                AutomaticDecompression = DecompressionMethods.All,
            };
            if (ClusterTls.CreateValidator(c, environment.ContentRootPath) is { } validator)
                handler.SslOptions.RemoteCertificateValidationCallback = validator;
            var baseAddress = c.ManagementUrl.ToString().EndsWith('/') ? c.ManagementUrl : new Uri(c.ManagementUrl + "/");
            var http = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = options.Value.RequestTimeout };
            return (http, new RabbitMqManagementClient(http, c), c);
        }, StringComparer.Ordinal);
    }

    public IEnumerable<ClusterOptions> Clusters => _clusters.Values.Select(c => c.Options);

    public bool Exists(string cluster) => _clusters.ContainsKey(cluster);

    public RabbitMqManagementClient Get(string cluster) =>
        _clusters.TryGetValue(cluster, out var c) ? c.Client : throw new KeyNotFoundException($"Unknown cluster '{cluster}'.");

    public void Dispose()
    {
        foreach (var c in _clusters.Values)
            c.Http.Dispose();
    }
}
