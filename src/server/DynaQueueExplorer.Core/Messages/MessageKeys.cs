using System.IO.Hashing;

namespace DynaQueueExplorer.Core.Messages;

/// <summary>
/// A stable identity for a message, computed the same way from the management API (browse) and from AMQP
/// (move/copy/export), so a selection made in the UI can be found again in the queue.
/// </summary>
public static class MessageKeys
{
    /// <summary>Bodies are hashed up to this size: the management API truncates payloads at the browse limit.</summary>
    public const int HashedBodyBytes = 256 * 1024;

    public static string Compute(string? nServiceBusMessageId, string? messageIdProperty, ReadOnlySpan<byte> body, long totalLength)
    {
        if (!string.IsNullOrEmpty(nServiceBusMessageId))
            return "nsb:" + nServiceBusMessageId;
        if (!string.IsNullOrEmpty(messageIdProperty))
            return "id:" + messageIdProperty;

        var prefix = body.Length > HashedBodyBytes ? body[..HashedBodyBytes] : body;
        return $"h:{XxHash3.HashToUInt64(prefix):x16}:{totalLength}";
    }
}
