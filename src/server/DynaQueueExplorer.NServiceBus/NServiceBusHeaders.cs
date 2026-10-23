namespace DynaQueueExplorer.NServiceBus;

/// <summary>Well-known NServiceBus header names.</summary>
public static class NServiceBusHeaders
{
    public const string Prefix = "NServiceBus.";
    public const string DiagnosticsPrefix = "$.diagnostics.";
    public const string ExceptionInfoPrefix = "NServiceBus.ExceptionInfo.";
    public const string ServiceControlPrefix = "ServiceControl.";

    public const string MessageId = "NServiceBus.MessageId";
    public const string CorrelationId = "NServiceBus.CorrelationId";
    public const string ConversationId = "NServiceBus.ConversationId";
    public const string RelatedTo = "NServiceBus.RelatedTo";
    public const string MessageIntent = "NServiceBus.MessageIntent";
    public const string EnclosedMessageTypes = "NServiceBus.EnclosedMessageTypes";
    public const string ContentType = "NServiceBus.ContentType";
    public const string NServiceBusVersion = "NServiceBus.Version";
    public const string TimeSent = "NServiceBus.TimeSent";
    public const string DeliverAt = "NServiceBus.DeliverAt";
    public const string TimeToBeReceived = "NServiceBus.TimeToBeReceived";
    public const string ReplyToAddress = "NServiceBus.ReplyToAddress";
    public const string OriginatingEndpoint = "NServiceBus.OriginatingEndpoint";
    public const string OriginatingMachine = "NServiceBus.OriginatingMachine";
    public const string ProcessingEndpoint = "NServiceBus.ProcessingEndpoint";
    public const string ProcessingMachine = "NServiceBus.ProcessingMachine";
    public const string ControlMessage = "NServiceBus.ControlMessage";

    public const string OriginatingHostId = "$.diagnostics.originating.hostid";
    public const string HostDisplayName = "$.diagnostics.hostdisplayname";

    public const string SagaId = "NServiceBus.SagaId";
    public const string SagaType = "NServiceBus.SagaType";
    public const string OriginatingSagaId = "NServiceBus.OriginatingSagaId";
    public const string OriginatingSagaType = "NServiceBus.OriginatingSagaType";

    public const string ExceptionType = "NServiceBus.ExceptionInfo.ExceptionType";
    public const string ExceptionMessage = "NServiceBus.ExceptionInfo.Message";
    public const string ExceptionSource = "NServiceBus.ExceptionInfo.Source";
    public const string ExceptionStackTrace = "NServiceBus.ExceptionInfo.StackTrace";
    public const string InnerExceptionType = "NServiceBus.ExceptionInfo.InnerExceptionType";
    public const string FailedQueue = "NServiceBus.FailedQ";
    public const string TimeOfFailure = "NServiceBus.TimeOfFailure";
    public const string ExceptionDataPrefix = "NServiceBus.ExceptionInfo.Data.";
    public const string FailedHandlerType = "NServiceBus.ExceptionInfo.Data.Handler type";

    /// <summary>Present while a message is being retried; removed when it is moved to the error queue.</summary>
    public const string DelayedRetries = "NServiceBus.Retries";
    public const string DelayedRetriesTimestamp = "NServiceBus.Retries.Timestamp";

    /// <summary>
    /// Headers describing a failed processing attempt. Dropped when a failed message is sent back to its queue for a
    /// retry, so the next attempt (and the audit copy) does not carry the details of the previous failure.
    /// </summary>
    public static bool IsFailureHeader(string key) =>
        key.StartsWith(ExceptionInfoPrefix, StringComparison.Ordinal)
        || key is FailedQueue or TimeOfFailure or DelayedRetries or DelayedRetriesTimestamp or DeliverAt
            or ProcessingEndpoint or ProcessingMachine or "NServiceBus.ProcessingStarted" or "NServiceBus.ProcessingEnded";

    /// <summary><c>Exception.Data</c> entries NServiceBus adds itself; any other entry comes from application code.</summary>
    public static readonly IReadOnlySet<string> BuiltInExceptionData = new HashSet<string>(StringComparer.Ordinal)
    {
        "Handler canceled", "Handler failure time", "Handler start time", "Handler type",
        "Message ID", "Message type", "Pipeline canceled", "Transport message ID",
    };
}
