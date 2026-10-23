namespace DynaQueueExplorer.Core.Messages;

public static class QueueNaming
{
    /// <summary>NServiceBus error queues: <c>error</c> or a name ending in <c>.error</c>.</summary>
    public static bool IsErrorQueue(string name) => name == "error" || name.EndsWith(".error", StringComparison.Ordinal);

    /// <summary>Audit queues receive copies of processed messages; nobody is expected to consume them in the explorer.</summary>
    public static bool IsAuditQueue(string name) => name == "audit" || name.EndsWith(".audit", StringComparison.Ordinal);
}
