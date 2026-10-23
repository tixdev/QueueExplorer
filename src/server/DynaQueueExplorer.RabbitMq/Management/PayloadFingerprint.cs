using System.IO.Hashing;

namespace DynaQueueExplorer.RabbitMq.Management;

/// <summary>Order-independent hash of several raw API payloads, used to detect unchanged snapshots.</summary>
public sealed class PayloadFingerprint
{
    private ulong _value;

    public ulong Value => Interlocked.Read(ref _value);

    // XOR keeps the result independent of the order in which the parallel calls complete.
    public void Append(ReadOnlySpan<byte> payload)
    {
        var hash = XxHash3.HashToUInt64(payload);
        ulong current, next;
        do
        {
            current = Interlocked.Read(ref _value);
            next = current ^ (hash * 0x9E3779B97F4A7C15UL + (ulong)payload.Length);
        } while (Interlocked.CompareExchange(ref _value, next, current) != current);
    }
}
