using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using DynaQueueExplorer.Core.Clusters;

namespace DynaQueueExplorer.RabbitMq.Management;

public sealed class RabbitMqApiException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>
/// Thin client over the management HTTP API of one cluster. All calls share the cluster's pooled
/// <see cref="HttpClient"/>, so the number of TCP connections towards RabbitMQ stays bounded.
/// </summary>
public sealed class RabbitMqManagementClient(HttpClient http, ClusterOptions cluster)
{
    // disable_stats skips the per-queue statistics, the most expensive part of /api/queues on large clusters.
    // It also drops the consumer count, which is derived from /api/consumers instead.
    private const string QueueColumns =
        "name,vhost,type,state,durable,auto_delete,exclusive,arguments,policy,messages,messages_ready,messages_unacknowledged";

    private const string ExchangeColumns = "name,vhost,type,durable,auto_delete,internal,arguments,policy";

    private readonly AuthenticationHeaderValue _authorization =
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cluster.Username}:{cluster.Password}")));

    public string ClusterName => cluster.Name;

    public Task<RmqOverview> GetOverviewAsync(CancellationToken ct, PayloadFingerprint? fingerprint = null) =>
        GetAsync("api/overview?columns=cluster_name,rabbitmq_version,erlang_version,product_name,product_version,queue_totals,object_totals,message_stats",
            RmqJsonContext.Default.RmqOverview, ct, fingerprint);

    public Task<List<RmqNode>> GetNodesAsync(CancellationToken ct, PayloadFingerprint? fingerprint = null) =>
        GetAsync("api/nodes?columns=name,type,running,uptime,processors,mem_used,mem_limit,mem_alarm,disk_free,disk_free_limit,disk_free_alarm,fd_used,fd_total,sockets_used,sockets_total,proc_used,proc_total,run_queue",
            RmqJsonContext.Default.ListRmqNode, ct, fingerprint);

    public Task<List<RmqVhost>> GetVhostsAsync(CancellationToken ct, PayloadFingerprint? fingerprint = null) =>
        GetAsync("api/vhosts?columns=name,description", RmqJsonContext.Default.ListRmqVhost, ct, fingerprint);

    public Task<List<RmqQueue>> GetQueuesAsync(CancellationToken ct, PayloadFingerprint? fingerprint = null) =>
        GetAsync($"api/queues?disable_stats=true&enable_queue_totals=true&columns={QueueColumns}", RmqJsonContext.Default.ListRmqQueue, ct, fingerprint);

    /// <summary>Single queue including message rates and the effective delivery limit; cheap enough to call on demand.</summary>
    public Task<RmqQueue> GetQueueAsync(string vhost, string name, CancellationToken ct) =>
        GetAsync($"api/queues/{Escape(vhost)}/{Escape(name)}?columns={QueueColumns},consumers,message_stats,delivery_limit", RmqJsonContext.Default.RmqQueue, ct);

    /// <summary>The fields that decide whether browsing a queue is safe, read live (tiny payload).</summary>
    public Task<RmqQueue> GetQueueBrowseSafetyAsync(string vhost, string name, CancellationToken ct) =>
        GetAsync($"api/queues/{Escape(vhost)}/{Escape(name)}?columns=name,vhost,type,messages,delivery_limit", RmqJsonContext.Default.RmqQueue, ct);

    /// <summary>Depths of the error queues only, filtered by the broker (a few hundred bytes, for the trend sampler).</summary>
    public async Task<List<RmqQueueDepth>> GetErrorQueueDepthsAsync(CancellationToken ct)
    {
        var name = Uri.EscapeDataString(@"(^|\.)error$");
        var page = await GetAsync($"api/queues?page=1&page_size=500&name={name}&use_regex=true&disable_stats=true&enable_queue_totals=true&columns=name,vhost,messages",
            RmqJsonContext.Default.RmqPageRmqQueueDepth, ct);
        return page.Items;
    }

    public Task<List<RmqConsumer>> GetConsumersAsync(CancellationToken ct, PayloadFingerprint? fingerprint = null) =>
        GetAsync("api/consumers?columns=queue.name,queue.vhost", RmqJsonContext.Default.ListRmqConsumer, ct, fingerprint);

    public Task<List<RmqExchange>> GetExchangesAsync(CancellationToken ct, PayloadFingerprint? fingerprint = null) =>
        GetAsync($"api/exchanges?disable_stats=true&columns={ExchangeColumns}", RmqJsonContext.Default.ListRmqExchange, ct, fingerprint);

    public Task<List<RmqBinding>> GetBindingsAsync(CancellationToken ct, PayloadFingerprint? fingerprint = null) =>
        GetAsync("api/bindings", RmqJsonContext.Default.ListRmqBinding, ct, fingerprint);

    /// <summary>Shovel status; returns an empty list when the shovel management plugin is not enabled.</summary>
    public Task<List<RmqShovelStatus>> GetShovelStatusesAsync(CancellationToken ct, PayloadFingerprint? fingerprint = null) =>
        GetOptionalListAsync("api/shovels", RmqJsonContext.Default.ListRmqShovelStatus, ct, fingerprint);

    public Task<List<RmqParameter>> GetShovelDefinitionsAsync(CancellationToken ct, PayloadFingerprint? fingerprint = null) =>
        GetOptionalListAsync("api/parameters/shovel", RmqJsonContext.Default.ListRmqParameter, ct, fingerprint);

    /// <summary>
    /// Fetches messages with ack_requeue_true: they are delivered and immediately requeued.
    /// This is not side-effect free (redelivered flag, ordering, quorum delivery-count).
    /// </summary>
    public async Task<List<RmqGetMessage>> GetMessagesAsync(string vhost, string queue, int count, int truncateBytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/queues/{Escape(vhost)}/{Escape(queue)}/get")
        {
            Content = JsonContent.Create(new RmqGetRequest(count, "ack_requeue_true", "auto", truncateBytes), RmqJsonContext.Default.RmqGetRequest),
        };
        return await SendAsync(request, RmqJsonContext.Default.ListRmqGetMessage, ct);
    }

    /// <summary>Removes every ready message of the queue (unacknowledged ones stay with their consumers).</summary>
    public async Task PurgeAsync(string vhost, string queue, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(http.Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/queues/{Escape(vhost)}/{Escape(queue)}/contents");
        request.Headers.Authorization = _authorization;
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, deadline.Token);
        }
        catch (HttpRequestException e)
        {
            throw new RabbitMqApiException($"Cluster '{cluster.Name}' is unreachable: {e.Message}", null, e);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new RabbitMqApiException($"Cluster '{cluster.Name}' returned {(int)response.StatusCode} purging {vhost}/{queue}: " +
                                               Truncate(await response.Content.ReadAsStringAsync(ct), 300), response.StatusCode);
        }
    }

    private Task<T> GetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct, PayloadFingerprint? fingerprint = null) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, path), type, ct, fingerprint);

    private async Task<List<T>> GetOptionalListAsync<T>(string path, JsonTypeInfo<List<T>> type, CancellationToken ct, PayloadFingerprint? fingerprint)
    {
        try
        {
            return await GetAsync(path, type, ct, fingerprint);
        }
        catch (RabbitMqApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, JsonTypeInfo<T> type, CancellationToken callerToken, PayloadFingerprint? fingerprint = null)
    {
        // HttpClient.Timeout stops at the response headers (ResponseHeadersRead): bound the body read as well, so a
        // stalled response can never hang a caller that has no deadline of its own.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(http.Timeout);
        var ct = deadline.Token;
        using (request)
        {
            request.Headers.Authorization = _authorization;
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException e)
            {
                throw new RabbitMqApiException($"Cluster '{cluster.Name}' is unreachable: {e.Message}", null, e);
            }
            catch (OperationCanceledException e) when (!callerToken.IsCancellationRequested)
            {
                throw new RabbitMqApiException($"Cluster '{cluster.Name}' did not answer within {http.Timeout.TotalSeconds:0} s ({request.RequestUri}).", null, e);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    throw new RabbitMqApiException(
                        $"Cluster '{cluster.Name}' returned {(int)response.StatusCode} for {request.RequestUri}: {Truncate(body, 300)}",
                        response.StatusCode);
                }

                // Buffered (not streamed) so the snapshot can fingerprint the raw payload without re-serializing.
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                fingerprint?.Append(bytes);
                return System.Text.Json.JsonSerializer.Deserialize(bytes, type)
                       ?? throw new RabbitMqApiException($"Cluster '{cluster.Name}' returned an empty body for {request.RequestUri}");
            }
        }
    }

    private static string Escape(string segment) => Uri.EscapeDataString(segment);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
