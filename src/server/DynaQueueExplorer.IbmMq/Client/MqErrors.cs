using DynaQueueExplorer.IbmMq.Formats;
using IBM.WMQ;

namespace DynaQueueExplorer.IbmMq.Client;

/// <summary>An IBM MQ failure with its reason code, constant name and meaning (never just a number).</summary>
public sealed class IbmMqException(string message, int reason, Exception? inner = null) : Exception(message, inner)
{
    public int Reason { get; } = reason;

    public bool IsNotAuthorized => Reason == MQC.MQRC_NOT_AUTHORIZED;

    public static IbmMqException From(MQException e, string operation) =>
        new($"{operation} failed: {MqReasons.Describe(e.ReasonCode)}", e.ReasonCode, e);
}

internal static class MqErrors
{
    /// <summary>Reasons after which the connection must be thrown away and re-established.</summary>
    public static bool BreaksConnection(int reason) => reason is
        MQC.MQRC_CONNECTION_BROKEN or MQC.MQRC_Q_MGR_NOT_AVAILABLE or MQC.MQRC_Q_MGR_QUIESCING or MQC.MQRC_Q_MGR_STOPPING
        or MQC.MQRC_CONNECTION_QUIESCING or MQC.MQRC_CONNECTION_STOPPING or MQC.MQRC_HCONN_ERROR or MQC.MQRC_RECONNECT_FAILED;
}
