using System.Threading.Channels;
using DynaQueueExplorer.Core.Messages;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace DynaQueueExplorer.RabbitMq.Amqp;

/// <summary>Which messages of a queue an operation applies to.</summary>
/// <param name="MaxCount">Without keys: the first N messages. With keys: an upper bound of messages scanned.</param>
/// <param name="Keys">Message keys (see <see cref="MessageKeys"/>) selected in the UI; null selects the first N.</param>
public sealed record MessageSelection(int MaxCount, IReadOnlySet<string>? Keys = null);

/// <summary>Where a message is published: a queue is ("", queue name).</summary>
public sealed record Destination(string Exchange, string RoutingKey)
{
    public static Destination Queue(string name) => new(string.Empty, name);

    public override string ToString() => Exchange.Length == 0 ? $"queue '{RoutingKey}'" : $"exchange '{Exchange}' ({RoutingKey})";
}

/// <summary>The destination of a message, or why it has none (it then stays in the source).</summary>
public sealed record MessageRoute(Destination? Destination, string? Error = null)
{
    public static MessageRoute To(Destination destination) => new(destination);

    public static MessageRoute None(string error) => new(null, error);
}

public sealed record OperationFailure(string Key, string Reason);

public sealed record TransferOutcome(int Scanned, int Selected, int Succeeded, IReadOnlyList<OperationFailure> Failures);

public sealed record PublishOutcome(int Succeeded, IReadOnlyList<(int Index, string Reason)> Failures);

/// <summary>
/// Moves, copies and reads messages over AMQP. A message is removed from its source only after the broker has
/// confirmed it at the destination; anything not confirmed, not selected or failing stays in the source queue.
/// </summary>
public sealed class MessageOperations(AmqpConnections connections, IOptions<RabbitMqOptions> options)
{
    private const int PublishBatchSize = 100;
    private static readonly TimeSpan DeliveryIdleTimeout = TimeSpan.FromMilliseconds(500);

    public static string KeyOf(RawMessage message)
    {
        string? nsbId = null;
        if (message.Properties.Headers?.TryGetValue("NServiceBus.MessageId", out var header) == true)
            nsbId = AmqpValueCodec.ToText(header);
        return MessageKeys.Compute(nsbId, message.Properties.MessageId, message.Body, message.Body.Length);
    }

    /// <summary>
    /// Takes the selected messages, hands them to <paramref name="commit"/> (store, export…) and then removes them
    /// (<paramref name="remove"/>) or puts them back. If <paramref name="commit"/> throws, everything is put back.
    /// </summary>
    public async Task<int> TakeAsync(
        string cluster, string vhost, string queue, MessageSelection selection, bool remove,
        Func<IReadOnlyList<RawMessage>, Task> commit, CancellationToken ct)
    {
        await using var session = await HoldAsync(cluster, vhost, queue, selection, ct);
        await commit(session.Selected.Select(h => h.Message).ToList());
        foreach (var held in session.Selected)
            await session.SettleAsync(held, remove);
        return session.Selected.Count;
    }

    /// <summary>Publishes each selected message to its destination, then removes it from the source (move) or not (copy).</summary>
    /// <param name="route">Destination of each message; a message without one stays in the source.</param>
    /// <param name="dropHeader">Headers to remove when republishing (broker-managed ones are always removed).</param>
    public async Task<TransferOutcome> TransferAsync(
        string cluster, string vhost, string queue, MessageSelection selection, bool remove,
        string destinationVhost, Func<RawMessage, MessageRoute> route, Func<string, bool>? dropHeader, CancellationToken ct)
    {
        await using var session = await HoldAsync(cluster, vhost, queue, selection, ct);
        var failures = new List<OperationFailure>();
        var routed = new List<(Held Held, Destination Destination)>();
        foreach (var held in session.Selected)
        {
            var result = route(held.Message);
            if (result.Destination is { } destination)
            {
                routed.Add((held, destination));
            }
            else
            {
                failures.Add(new OperationFailure(held.Key, result.Error ?? "No destination for this message."));
                await session.SettleAsync(held, remove: false);
            }
        }

        var published = await PublishAsync(cluster, destinationVhost,
            routed.Select(r => (r.Held.Message, r.Destination)).ToList(), dropHeader, ct);
        var failedIndexes = published.Failures.ToDictionary(f => f.Index, f => f.Reason);
        for (var i = 0; i < routed.Count; i++)
        {
            if (failedIndexes.TryGetValue(i, out var reason))
            {
                failures.Add(new OperationFailure(routed[i].Held.Key, reason));
                await session.SettleAsync(routed[i].Held, remove: false);
            }
            else
            {
                await session.SettleAsync(routed[i].Held, remove);
            }
        }
        return new TransferOutcome(session.Scanned, session.Selected.Count, published.Succeeded, failures);
    }

    /// <summary>
    /// Publishes with mandatory routing and publisher confirms, in concurrent batches. A message counts as
    /// published only once the broker confirmed it; unroutable or nacked messages are reported as failures.
    /// </summary>
    public async Task<PublishOutcome> PublishAsync(
        string cluster, string vhost, IReadOnlyList<(RawMessage Message, Destination Destination)> messages,
        Func<string, bool>? dropHeader, CancellationToken ct)
    {
        if (messages.Count == 0)
            return new PublishOutcome(0, []);

        var connection = await connections.GetAsync(cluster, vhost, ct);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);

        var failures = new List<(int, string)>();
        var succeeded = 0;
        for (var start = 0; start < messages.Count; start += PublishBatchSize)
        {
            var batch = Enumerable.Range(start, Math.Min(PublishBatchSize, messages.Count - start))
                .Select(i => (Index: i, Task: PublishOneAsync(channel, messages[i].Message, messages[i].Destination, dropHeader, ct)))
                .ToList();
            foreach (var (index, task) in batch)
            {
                var error = await task;
                if (error is null)
                    succeeded++;
                else
                    failures.Add((index, error));
            }
        }
        return new PublishOutcome(succeeded, failures);
    }

    private static async Task<string?> PublishOneAsync(IChannel channel, RawMessage message, Destination destination, Func<string, bool>? dropHeader, CancellationToken ct)
    {
        try
        {
            await channel.BasicPublishAsync(destination.Exchange, destination.RoutingKey, mandatory: true,
                AmqpProperties.ToAmqp(message.Properties, dropHeader), message.Body, ct);
            return null;
        }
        catch (PublishReturnException e)
        {
            return $"Not routable to {destination}: {e.ReplyText}";
        }
        catch (PublishException)
        {
            return $"Rejected by the broker at {destination}.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Consumes up to the needed number of messages without acknowledging them, so they stay locked until
    /// settled. A consumer with a matching prefetch gets them in bulk instead of one round-trip per basic.get.
    /// </summary>
    private async Task<HoldSession> HoldAsync(string cluster, string vhost, string queue, MessageSelection selection, CancellationToken ct)
    {
        var connection = await connections.GetAsync(cluster, vhost, ct);
        var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        var session = new HoldSession(channel);
        try
        {
            var ready = (await channel.QueueDeclarePassiveAsync(queue, ct)).MessageCount;
            var limit = (int)Math.Min(Math.Min(ready, (uint)options.Value.MaxMessagesPerOperation),
                selection.Keys is null ? (uint)Math.Max(0, selection.MaxCount) : uint.MaxValue);
            if (limit == 0)
                return session;

            await channel.BasicQosAsync(0, (ushort)Math.Min(limit, ushort.MaxValue), global: false, ct);
            var deliveries = Channel.CreateUnbounded<BasicDeliverEventArgs>();
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, args) =>
            {
                // The body buffer is only valid during the callback.
                deliveries.Writer.TryWrite(new BasicDeliverEventArgs(args.ConsumerTag, args.DeliveryTag, args.Redelivered,
                    args.Exchange, args.RoutingKey, args.BasicProperties, args.Body.ToArray(), args.CancellationToken));
                return Task.CompletedTask;
            };
            var consumerTag = await channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);

            var wanted = selection.Keys;
            var found = 0;
            while (session.Scanned < limit && (wanted is null || found < wanted.Count))
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(DeliveryIdleTimeout);
                BasicDeliverEventArgs delivery;
                try
                {
                    delivery = await deliveries.Reader.ReadAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break; // Other consumers took the rest, or the queue drained meanwhile.
                }

                var message = new RawMessage(delivery.Exchange, delivery.RoutingKey, delivery.Redelivered,
                    AmqpProperties.FromAmqp(delivery.BasicProperties), delivery.Body.ToArray());
                var held = new Held(delivery.DeliveryTag, message, KeyOf(message));
                session.Scanned++;
                if (wanted is null || wanted.Contains(held.Key))
                {
                    session.Selected.Add(held);
                    found++;
                }
                else
                {
                    session.Others.Add(held);
                }
            }

            await channel.BasicCancelAsync(consumerTag, cancellationToken: ct);
            // Messages delivered after the loop ended are part of nothing: give them back right away.
            while (deliveries.Reader.TryRead(out var extra))
                await channel.BasicNackAsync(extra.DeliveryTag, multiple: false, requeue: true, ct);
            foreach (var other in session.Others)
                await session.SettleAsync(other, remove: false);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private sealed record Held(ulong DeliveryTag, RawMessage Message, string Key);

    /// <summary>Unsettled messages are requeued by the broker when the channel closes, even on failure.</summary>
    private sealed class HoldSession(IChannel channel) : IAsyncDisposable
    {
        public List<Held> Selected { get; } = [];
        public List<Held> Others { get; } = [];
        public int Scanned { get; set; }

        public async Task SettleAsync(Held held, bool remove)
        {
            if (remove)
                await channel.BasicAckAsync(held.DeliveryTag, multiple: false);
            else
                await channel.BasicNackAsync(held.DeliveryTag, multiple: false, requeue: true);
        }

        public async ValueTask DisposeAsync()
        {
            await channel.CloseAsync();
            await channel.DisposeAsync();
        }
    }
}
