namespace DynaQueueExplorer.NServiceBus;

public enum NServiceBusMessageKind
{
    Message,
    FailedMessage,
    ControlMessage,
    Subscription,
}

/// <summary>A message type parsed from an assembly-qualified name in <c>NServiceBus.EnclosedMessageTypes</c>.</summary>
public sealed record MessageTypeName(string FullName, string Name, string? Namespace, string? Assembly, string? Version);

/// <param name="Handler">The handler or saga that threw (NServiceBus 9.1+).</param>
/// <param name="DelayedRetries">Delayed retries performed so far; only present while the message is still being retried.</param>
/// <param name="LastDelayedRetryAt">When the last delayed retry was scheduled; kept on messages moved to the error queue.</param>
/// <param name="Data">Application entries of <c>Exception.Data</c>.</param>
public sealed record NServiceBusFailure(
    string? ExceptionType,
    string? Message,
    string? Source,
    string? StackTrace,
    string? InnerExceptionType,
    string? FailedQueue,
    DateTimeOffset? TimeOfFailure,
    string? ProcessingEndpoint,
    string? ProcessingMachine,
    string? Handler,
    int? DelayedRetries,
    DateTimeOffset? LastDelayedRetryAt,
    IReadOnlyDictionary<string, string?> Data);

public sealed record NServiceBusSaga(string? SagaId, string? SagaType, string? OriginatingSagaId, string? OriginatingSagaType);

public sealed record HeaderEntry(string Key, string Label, string? Value);

public sealed record HeaderGroup(string Name, IReadOnlyList<HeaderEntry> Headers);

public sealed record NServiceBusMessageInfo(
    NServiceBusMessageKind Kind,
    string? MessageId,
    string? Intent,
    IReadOnlyList<MessageTypeName> MessageTypes,
    string? ConversationId,
    string? CorrelationId,
    string? RelatedTo,
    string? ContentType,
    string? NServiceBusVersion,
    DateTimeOffset? TimeSent,
    DateTimeOffset? DeliverAt,
    string? TimeToBeReceived,
    string? OriginatingEndpoint,
    string? OriginatingMachine,
    string? OriginatingHost,
    string? ReplyToAddress,
    NServiceBusSaga? Saga,
    NServiceBusFailure? Failure,
    IReadOnlyList<HeaderGroup> HeaderGroups);
