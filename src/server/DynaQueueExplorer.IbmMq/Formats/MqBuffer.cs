using System.Buffers.Binary;
using System.Text;

namespace DynaQueueExplorer.IbmMq.Formats;

/// <summary>Reads IBM MQ structures: integers in the given encoding, fixed-length blank-padded strings in a CCSID.</summary>
internal ref struct MqReader(ReadOnlySpan<byte> data, bool bigEndian, Encoding text)
{
    private readonly ReadOnlySpan<byte> _data = data;
    public int Offset;

    public readonly int Remaining => _data.Length - Offset;

    public int Int32()
    {
        var span = _data.Slice(Offset, 4);
        Offset += 4;
        return bigEndian ? BinaryPrimitives.ReadInt32BigEndian(span) : BinaryPrimitives.ReadInt32LittleEndian(span);
    }

    public long Int64()
    {
        var span = _data.Slice(Offset, 8);
        Offset += 8;
        return bigEndian ? BinaryPrimitives.ReadInt64BigEndian(span) : BinaryPrimitives.ReadInt64LittleEndian(span);
    }

    public string String(int length)
    {
        var value = text.GetString(_data.Slice(Offset, length)).TrimEnd(' ', '\0');
        Offset += length;
        return value;
    }

    public byte[] Bytes(int length)
    {
        var value = _data.Slice(Offset, length).ToArray();
        Offset += length;
        return value;
    }
}

/// <summary>Builds IBM MQ structures; used by tests and the local seeder to produce real-looking messages.</summary>
public sealed class MqWriter(bool bigEndian = false, Encoding? text = null)
{
    private readonly MemoryStream _buffer = new();
    private readonly Encoding _text = text ?? Encoding.ASCII;

    public int Length => (int)_buffer.Length;

    public MqWriter Int32(int value)
    {
        Span<byte> span = stackalloc byte[4];
        if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(span, value);
        else BinaryPrimitives.WriteInt32LittleEndian(span, value);
        _buffer.Write(span);
        return this;
    }

    public MqWriter String(string value, int length)
    {
        var bytes = _text.GetBytes(value.PadRight(length));
        _buffer.Write(bytes, 0, length);
        return this;
    }

    public MqWriter Bytes(ReadOnlySpan<byte> bytes)
    {
        _buffer.Write(bytes);
        return this;
    }

    public byte[] ToArray() => _buffer.ToArray();

    /// <summary>Dead-letter header (MQDLH v1, 172 bytes), followed by data in <paramref name="format"/>.</summary>
    public MqWriter DeadLetterHeader(int reason, string destQueue, string destQmgr, string format, int ccsid, int encoding,
        string putApplication = "amqrmppa", DateTime? putTime = null)
    {
        var at = putTime ?? DateTime.UtcNow;
        return String("DLH ", 4).Int32(1).Int32(reason).String(destQueue, 48).String(destQmgr, 48).Int32(encoding).Int32(ccsid)
            .String(format, 8).Int32(MqPutApplType.Qmgr).String(putApplication, 28).String(at.ToString("yyyyMMdd"), 8)
            .String(at.ToString("HHmmssff"), 8);
    }

    /// <summary>RFH2 header with the given folders (XML), followed by data in <paramref name="format"/>.</summary>
    public MqWriter Rfh2(string format, int ccsid, int encoding, params string[] folders)
    {
        var data = folders.Select(f =>
        {
            var bytes = Encoding.UTF8.GetBytes(f);
            var padded = (bytes.Length + 3) / 4 * 4;
            return bytes.Concat(Enumerable.Repeat((byte)' ', padded - bytes.Length)).ToArray();
        }).ToList();
        var length = 36 + data.Sum(d => 4 + d.Length);
        String("RFH ", 4).Int32(2).Int32(length).Int32(encoding).Int32(ccsid).String(format, 8).Int32(0).Int32(1208);
        foreach (var d in data)
            Int32(d.Length).Bytes(d);
        return this;
    }

    /// <summary>Transmission queue header (MQXQH) with an embedded MQMD v1.</summary>
    public MqWriter TransmissionHeader(string remoteQueue, string remoteQmgr, string format, int ccsid, int encoding, string putApplication, DateTime? putTime = null)
    {
        var at = putTime ?? DateTime.UtcNow;
        String("XQH ", 4).Int32(1).String(remoteQueue, 48).String(remoteQmgr, 48);
        // MQMD version 1
        String("MD  ", 4).Int32(1).Int32(0).Int32(8).Int32(-1).Int32(0).Int32(encoding).Int32(ccsid).String(format, 8)
            .Int32(0).Int32(1).Bytes(new byte[24]).Bytes(new byte[24]).Int32(0).String("", 48).String("", 48).String("app", 12)
            .Bytes(new byte[32]).String("", 32).Int32(28).String(putApplication, 28).String(at.ToString("yyyyMMdd"), 8)
            .String(at.ToString("HHmmssff"), 8).String("", 4);
        return this;
    }
}

internal static class MqPutApplType
{
    public const int Qmgr = 7;
}
