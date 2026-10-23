using System.Runtime.InteropServices;

namespace DynaQueueExplorer.MsmqAgent.Msmq;

/// <summary>Late-bound access to the MSMQ COM objects (mqoa.dll), installed with every MSMQ.</summary>
internal static class MsmqCom
{
    public const int PeekAccess = 32;   // MQ_PEEK_ACCESS
    public const int DenyNone = 0;      // MQ_DENY_NONE

    public static dynamic Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: false)
                   ?? throw new MsmqException($"{progId} is not registered: is Message Queuing installed on the agent's machine?");
        return Activator.CreateInstance(type)!;
    }

    public static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
            Marshal.FinalReleaseComObject(com);
    }

    /// <summary>A property that may be missing on older MSMQ versions or for some messages.</summary>
    public static T? Try<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            return default;
        }
    }

    /// <summary>Error text with the MSMQ error name, e.g. "MQ_ERROR_ACCESS_DENIED (0xC00E0025): …".</summary>
    public static string Describe(COMException e)
    {
        var code = unchecked((uint)e.HResult);
        var name = code switch
        {
            0xC00E0003 => "MQ_ERROR_QUEUE_NOT_FOUND",
            0xC00E0004 => "MQ_ERROR_QUEUE_NOT_ACTIVE",
            0xC00E000B => "MQ_ERROR_SERVICE_NOT_AVAILABLE",
            0xC00E001B => "MQ_ERROR_IO_TIMEOUT",
            0xC00E001E => "MQ_ERROR_ILLEGAL_FORMATNAME",
            0xC00E0020 => "MQ_ERROR_UNSUPPORTED_FORMATNAME_OPERATION",
            0xC00E0025 => "MQ_ERROR_ACCESS_DENIED",
            0xC00E0069 => "MQ_ERROR_REMOTE_MACHINE_NOT_AVAILABLE",
            0xC00E0070 => "MQ_ERROR_UNSUPPORTED_OPERATION",
            0xC00E0013 => "MQ_ERROR_NO_DS",
            _ => null,
        };
        return name is null ? $"0x{code:X8}: {e.Message.Trim()}" : $"{name} (0x{code:X8}): {e.Message.Trim()}";
    }

    public static bool IsAccessDenied(COMException e) => unchecked((uint)e.HResult) == 0xC00E0025;
}

/// <summary>An MSMQ failure reported to the explorer with its MSMQ error name.</summary>
public sealed class MsmqException(string message, bool accessDenied = false, Exception? inner = null) : Exception(message, inner)
{
    public bool AccessDenied { get; } = accessDenied;

    public static MsmqException From(COMException e, string what) => new($"{what}: {MsmqCom.Describe(e)}", MsmqCom.IsAccessDenied(e), e);
}
