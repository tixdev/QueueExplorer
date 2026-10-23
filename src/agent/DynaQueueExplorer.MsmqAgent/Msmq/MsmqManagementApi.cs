using System.Runtime.InteropServices;

namespace DynaQueueExplorer.MsmqAgent.Msmq;

/// <summary>
/// MQMgmtGetInfo (mqrt.dll) for what the COM objects do not expose late-bound: the state and next hops of outgoing
/// queues. One call reads several properties of a queue.
/// </summary>
internal static partial class MsmqManagementApi
{
    // PROPID_MGMT_QUEUE_* (mq.h).
    public const uint MessageCount = 7;
    public const uint UsedQuota = 8;
    public const uint State = 11;
    public const uint NextHops = 12;

    private const ushort VtNull = 1;
    private const ushort VtUi4 = 19;
    private const ushort VtLpwstr = 31;
    private const ushort VtVector = 0x1000;
    private const int PropVariantSize = 24;

    public sealed record QueueInfo(long? Messages, long? Bytes, string? State, IReadOnlyList<string> NextHops);

    public static QueueInfo GetQueueInfo(string machine, string formatName)
    {
        uint[] ids = [MessageCount, UsedQuota, State, NextHops];
        var propIds = Marshal.AllocHGlobal(sizeof(uint) * ids.Length);
        var values = Marshal.AllocHGlobal(PropVariantSize * ids.Length);
        var status = Marshal.AllocHGlobal(sizeof(int) * ids.Length);
        try
        {
            for (var i = 0; i < ids.Length; i++)
            {
                Marshal.WriteInt32(propIds, i * sizeof(uint), unchecked((int)ids[i]));
                for (var b = 0; b < PropVariantSize; b += 8)
                    Marshal.WriteInt64(values, i * PropVariantSize + b, 0);
                // VT_NULL: MSMQ allocates the value, released with MQFreeMemory.
                Marshal.WriteInt16(values, i * PropVariantSize, unchecked((short)VtNull));
            }
            var props = new MgmtProps { Count = (uint)ids.Length, PropIds = propIds, Values = values, Status = status };
            var hr = MQMgmtGetInfo(machine, "QUEUE=" + formatName, ref props);
            if (hr < 0)
                throw new COMException("MQMgmtGetInfo failed", hr);

            long? messages = null, bytes = null;
            string? state = null;
            var hops = new List<string>();
            for (var i = 0; i < ids.Length; i++)
            {
                var value = values + i * PropVariantSize;
                var vt = (ushort)Marshal.ReadInt16(value);
                switch (ids[i])
                {
                    case MessageCount when vt == VtUi4:
                        messages = (uint)Marshal.ReadInt32(value, 8);
                        break;
                    case UsedQuota when vt == VtUi4:
                        bytes = (uint)Marshal.ReadInt32(value, 8);
                        break;
                    case State when vt == VtLpwstr:
                        state = TakeString(Marshal.ReadIntPtr(value, 8));
                        break;
                    case NextHops when vt == (VtVector | VtLpwstr):
                        var count = Marshal.ReadInt32(value, 8);
                        var items = Marshal.ReadIntPtr(value, 16);
                        for (var h = 0; h < count; h++)
                            if (TakeString(Marshal.ReadIntPtr(items, h * IntPtr.Size)) is { } hop)
                                hops.Add(hop);
                        if (items != IntPtr.Zero)
                            MQFreeMemory(items);
                        break;
                }
            }
            return new QueueInfo(messages, bytes, state?.ToLowerInvariant(), hops);
        }
        finally
        {
            Marshal.FreeHGlobal(propIds);
            Marshal.FreeHGlobal(values);
            Marshal.FreeHGlobal(status);
        }
    }

    private static string? TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero)
            return null;
        var s = Marshal.PtrToStringUni(p);
        MQFreeMemory(p);
        return s;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MgmtProps
    {
        public uint Count;
        public IntPtr PropIds;
        public IntPtr Values;
        public IntPtr Status;
    }

    [LibraryImport("mqrt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MQMgmtGetInfo(string machine, string objectName, ref MgmtProps props);

    [LibraryImport("mqrt.dll")]
    private static partial void MQFreeMemory(IntPtr memory);
}
