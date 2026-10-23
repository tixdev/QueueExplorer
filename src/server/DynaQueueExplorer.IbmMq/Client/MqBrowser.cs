using IBM.WMQ;
using Microsoft.Extensions.Options;

namespace DynaQueueExplorer.IbmMq.Client;

/// <summary>A browsed message: the MQMD fields and the raw bytes (headers included, no conversion), possibly truncated.</summary>
public sealed record MqRawMessage(
    byte[] MessageId,
    byte[] CorrelationId,
    string Format,
    int Ccsid,
    int Encoding,
    int Persistence,
    int Priority,
    int MessageType,
    int Feedback,
    int Expiry,
    int BackoutCount,
    string PutApplicationName,
    int PutApplicationType,
    DateTimeOffset PutTime,
    string ReplyToQueue,
    string ReplyToQueueManager,
    string UserId,
    int Length,
    byte[] Data);

/// <summary>
/// Browses queues with MQGMO_BROWSE: reading does not remove, lock or change messages. Properties are forced into an
/// RFH2 header and no data conversion is asked, so headers and bodies are shown exactly as stored.
/// </summary>
public sealed class MqBrowser(IbmMqConnections connections, IOptions<IbmMqOptions> options)
{
    public Task<List<MqRawMessage>> BrowseAsync(string cluster, string queue, int count, CancellationToken ct) =>
        connections.RunAsync(cluster, qm =>
        {
            var truncate = options.Value.BrowseTruncateBytes;
            var result = new List<MqRawMessage>();
            var q = qm.AccessQueue(queue, MQC.MQOO_BROWSE | MQC.MQOO_FAIL_IF_QUIESCING);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var message = new MQMessage();
                    var gmo = new MQGetMessageOptions
                    {
                        Options = (i == 0 ? MQC.MQGMO_BROWSE_FIRST : MQC.MQGMO_BROWSE_NEXT) | MQC.MQGMO_NO_WAIT
                                  | MQC.MQGMO_FAIL_IF_QUIESCING | MQC.MQGMO_PROPERTIES_FORCE_MQRFH2,
                    };
                    try
                    {
                        q.Get(message, gmo);
                    }
                    catch (MQException e) when (e.ReasonCode == MQC.MQRC_NO_MSG_AVAILABLE)
                    {
                        break;
                    }
                    var length = message.MessageLength;
                    var data = message.ReadBytes(Math.Min(length, truncate));
                    result.Add(new MqRawMessage(message.MessageId, message.CorrelationId, message.Format, message.CharacterSet, message.Encoding,
                        message.Persistence, message.Priority, message.MessageType, message.Feedback, message.Expiry, message.BackoutCount,
                        message.PutApplicationName.Trim(), message.PutApplicationType, new DateTimeOffset(DateTime.SpecifyKind(message.PutDateTime, DateTimeKind.Utc)),
                        message.ReplyToQueueName.Trim(), message.ReplyToQueueManagerName.Trim(), message.UserId.Trim(), length, data));
                }
            }
            finally
            {
                q.Close();
            }
            return result;
        }, ct);
}
