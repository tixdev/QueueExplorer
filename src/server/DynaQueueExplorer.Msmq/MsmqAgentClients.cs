using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynaQueueExplorer.Core.Clusters;
using DynaQueueExplorer.Core.Messages;
using DynaQueueExplorer.Core.Msmq;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.Msmq;

/// <summary>
/// The MSMQ machines of the configuration and the agents that read them: one pooled HTTP client per agent, shared by
/// the machines it serves.
/// </summary>
public sealed class MsmqAgentClients : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, ClusterOptions> _machines;
    private readonly Dictionary<string, HttpClient> _agents = new(StringComparer.OrdinalIgnoreCase);

    public MsmqAgentClients(IOptions<ClustersOptions> clusters, IOptions<MsmqOptions> options)
    {
        _machines = clusters.Value.Items.Where(c => c.Broker == BrokerTypes.Msmq).ToDictionary(c => c.Name, StringComparer.Ordinal);
        foreach (var c in _machines.Values)
        {
            if (c.AgentUrl is null || string.IsNullOrEmpty(c.Host) || string.IsNullOrEmpty(c.AgentApiKey))
                throw new InvalidOperationException($"MSMQ connection '{c.Name}' needs Host (the MSMQ machine), AgentUrl and AgentApiKey.");
            var key = $"{c.AgentUrl}|{c.AgentApiKey}";
            if (_agents.ContainsKey(key))
                continue;
            var http = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = 8,
                AutomaticDecompression = DecompressionMethods.All,
            })
            {
                BaseAddress = c.AgentUrl.AbsoluteUri.EndsWith('/') ? c.AgentUrl : new Uri(c.AgentUrl.AbsoluteUri + "/"),
                Timeout = options.Value.RequestTimeout,
            };
            http.DefaultRequestHeaders.Add(MsmqAgentApi.KeyHeader, c.AgentApiKey);
            _agents[key] = http;
        }
    }

    public IReadOnlyCollection<ClusterOptions> Machines => _machines.Values;

    public bool Exists(string cluster) => _machines.ContainsKey(cluster);

    public ClusterOptions Options(string cluster) =>
        _machines.TryGetValue(cluster, out var c) ? c : throw new KeyNotFoundException($"Unknown MSMQ connection '{cluster}'.");

    /// <summary>Short machine name, used where RabbitMQ has a vhost (lists, URLs and RBAC patterns).</summary>
    public string MachineLabel(string cluster) => Options(cluster).Host!.Split('.')[0].ToLowerInvariant();

    public async Task<MsmqQueueListResponse> ListQueuesAsync(string cluster, CancellationToken ct)
    {
        var c = Options(cluster);
        using var response = await Client(c).GetAsync(MsmqAgentApi.Queues(c.Host!), ct);
        return await ReadAsync<MsmqQueueListResponse>(response, c, ct);
    }

    public async Task<MsmqBrowseResponse> BrowseAsync(string cluster, MsmqBrowseRequest request, CancellationToken ct)
    {
        var c = Options(cluster);
        using var response = await Client(c).PostAsJsonAsync(MsmqAgentApi.Browse(c.Host!), request, Json, ct);
        return await ReadAsync<MsmqBrowseResponse>(response, c, ct);
    }

    private HttpClient Client(ClusterOptions c) => _agents[$"{c.AgentUrl}|{c.AgentApiKey}"];

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, ClusterOptions c, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new MsmqAgentException("The MSMQ agent returned an empty answer.", response.StatusCode);

        // The agent answers with problem details carrying the MSMQ error name.
        string? detail = null;
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            detail = problem.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
        }
        catch (JsonException)
        {
        }
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => $"The MSMQ agent at {c.AgentUrl} refused the explorer's key (AgentApiKey).",
            HttpStatusCode.Forbidden => detail ?? $"The MSMQ agent may not read {c.Host}.",
            _ => detail ?? $"The MSMQ agent at {c.AgentUrl} answered {(int)response.StatusCode}.",
        };
        throw new MsmqAgentException(message, response.StatusCode);
    }

    public void Dispose()
    {
        foreach (var http in _agents.Values)
            http.Dispose();
    }
}

public sealed class MsmqAgentException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
