using System.Text;
using System.Xml.Linq;

namespace DynaQueueExplorer.IbmMq.Formats;

/// <summary>Well-known values of MQMD.Format (8 characters, blank padded).</summary>
public static class MqFormat
{
    public const string None = "        ";
    public const string String = "MQSTR   ";
    public const string Rfh2 = "MQHRF2  ";
    public const string Rfh = "MQHRF   ";
    public const string DeadLetter = "MQDEAD  ";
    public const string Transmission = "MQXMIT  ";
    public const string Admin = "MQADMIN ";
    public const string Pcf = "MQPCF   ";
    public const string Event = "MQEVENT ";
    public const string Cics = "MQCICS  ";
    public const string Ims = "MQIMS   ";
    public const string Trigger = "MQTRIG  ";

    public static string Normalize(string? format) => (format ?? "").PadRight(8)[..8];

    public static bool IsPcf(string format) => format is Admin or Pcf or Event;

    public static bool IsText(string format) => format is String;
}

/// <summary>A property of an RFH2 folder, e.g. jms/Dst or usr/OrderType (dt = RFH2 data type when declared).</summary>
public sealed record MqProperty(string Name, string? Value, string? Type);

public sealed record Rfh2Folder(string Name, string Xml, IReadOnlyList<MqProperty> Properties);

/// <summary>One header in the chain, with its fields in display order.</summary>
public sealed record MqHeader(string Format, string Title, IReadOnlyList<MqProperty> Fields, IReadOnlyList<Rfh2Folder> Folders);

/// <param name="BodyOffset">Where the application data starts after the last header.</param>
public sealed record MqParsedMessage(
    IReadOnlyList<MqHeader> Headers,
    string BodyFormat,
    int BodyCcsid,
    int BodyEncoding,
    int BodyOffset,
    string? Warning);

/// <summary>
/// Follows the MQ header chain: MQMD.Format names the first header, each header names the next and declares the
/// encoding and CCSID of what follows. Unknown formats end the chain; the rest is the application body.
/// </summary>
public static class MqHeaderChain
{
    public const int Mqenc_Integer_Mask = 0x0F;
    public const int Mqenc_Integer_Normal = 1; // big-endian
    public const int Mqenc_Integer_Reversed = 2; // little-endian

    public static bool IsBigEndian(int encoding) => (encoding & Mqenc_Integer_Mask) != Mqenc_Integer_Reversed;

    public static MqParsedMessage Parse(ReadOnlySpan<byte> data, string format, int ccsid, int encoding)
    {
        var headers = new List<MqHeader>();
        var offset = 0;
        format = MqFormat.Normalize(format);
        try
        {
            for (var guard = 0; guard < 16; guard++)
            {
                var text = Ccsids.GetEncoding(ccsid) ?? Encoding.ASCII;
                var reader = new MqReader(data[offset..], IsBigEndian(encoding), text);
                MqHeader header;
                (string Format, int Ccsid, int Encoding, int Length) next;
                switch (format)
                {
                    case MqFormat.DeadLetter:
                        (header, next) = DeadLetter(ref reader);
                        break;
                    case MqFormat.Rfh2:
                        (header, next) = Rfh2(ref reader, data[offset..]);
                        break;
                    case MqFormat.Rfh:
                        (header, next) = Rfh1(ref reader);
                        break;
                    case MqFormat.Transmission:
                        (header, next) = Transmission(ref reader);
                        break;
                    default:
                        return new MqParsedMessage(headers, format, ccsid, encoding, offset, null);
                }
                headers.Add(header);
                offset += next.Length;
                (format, ccsid, encoding) = (MqFormat.Normalize(next.Format), next.Ccsid, next.Encoding);
            }
            return new MqParsedMessage(headers, format, ccsid, encoding, offset, "Header chain too long; the rest is shown as body.");
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or FormatException or System.Xml.XmlException)
        {
            // A truncated or malformed header: show what was understood and the rest as raw body.
            return new MqParsedMessage(headers, format, ccsid, encoding, offset, $"The {format.Trim()} header could not be decoded ({e.Message}).");
        }
    }

    private static (MqHeader, (string, int, int, int)) DeadLetter(ref MqReader r)
    {
        var strucId = r.String(4);
        if (strucId != "DLH ".Trim())
            throw new FormatException($"expected StrucId 'DLH ', found '{strucId}'");
        var version = r.Int32();
        var reason = r.Int32();
        var destQ = r.String(48);
        var destQmgr = r.String(48);
        var encoding = r.Int32();
        var ccsid = r.Int32();
        var format = r.String(8);
        var applType = r.Int32();
        var applName = r.String(28);
        var putDate = r.String(8);
        var putTime = r.String(8);
        var fields = new List<MqProperty>
        {
            new("Reason", MqReasons.Describe(reason), null),
            new("Destination", destQmgr.Length > 0 ? $"{destQ}@{destQmgr}" : destQ, null),
            new("Put by", $"{applName} (type {applType})", null),
            new("Put at", FormatTimestamp(putDate, putTime), null),
            new("Next format", format, null),
            new("Next CCSID", Ccsids.Describe(ccsid), null),
            new("Version", version.ToString(), null),
        };
        return (new MqHeader(MqFormat.DeadLetter, "Dead-letter header", fields, []), (format, ccsid, encoding, 172));
    }

    private static (MqHeader, (string, int, int, int)) Rfh2(ref MqReader r, ReadOnlySpan<byte> data)
    {
        var strucId = r.String(4);
        if (strucId != "RFH")
            throw new FormatException($"expected StrucId 'RFH ', found '{strucId}'");
        var version = r.Int32();
        if (version != 2)
            throw new FormatException($"RFH2 with version {version}");
        var length = r.Int32();
        var encoding = r.Int32();
        var ccsid = r.Int32();
        var format = r.String(8);
        var flags = r.Int32();
        var nameValueCcsid = r.Int32();
        var folderText = Ccsids.GetEncoding(nameValueCcsid) ?? Encoding.UTF8;
        var folders = new List<Rfh2Folder>();
        while (r.Offset + 4 <= length)
        {
            var size = r.Int32();
            if (size <= 0 || r.Offset + size > length)
                break;
            var xml = folderText.GetString(data.Slice(r.Offset, size)).Trim(' ', '\0');
            r.Offset += size;
            folders.Add(ParseFolder(xml));
        }
        var fields = new List<MqProperty>
        {
            new("Next format", format, null),
            new("Next CCSID", Ccsids.Describe(ccsid), null),
            new("Folder CCSID", Ccsids.Describe(nameValueCcsid), null),
            new("Flags", flags.ToString(), null),
            new("Length", length.ToString(), null),
        };
        return (new MqHeader(MqFormat.Rfh2, "RFH2 (message properties)", fields, folders), (format, ccsid, encoding, length));
    }

    private static Rfh2Folder ParseFolder(string xml)
    {
        var root = XElement.Parse(xml);
        var properties = new List<MqProperty>();
        void Walk(XElement e, string path)
        {
            foreach (var child in e.Elements())
            {
                var name = path.Length == 0 ? child.Name.LocalName : $"{path}.{child.Name.LocalName}";
                if (child.HasElements)
                    Walk(child, name);
                else
                {
                    // xsi:nil="true" marks a null property; match by local name, the namespace prefix varies.
                    var isNull = child.Attributes().Any(a => a.Name.LocalName == "nil" && a.Value == "true");
                    properties.Add(new MqProperty(name, isNull ? null : child.Value, (string?)child.Attribute("dt")));
                }
            }
        }
        Walk(root, "");
        return new Rfh2Folder(root.Name.LocalName, xml, properties);
    }

    private static (MqHeader, (string, int, int, int)) Rfh1(ref MqReader r)
    {
        var strucId = r.String(4);
        if (strucId != "RFH")
            throw new FormatException($"expected StrucId 'RFH ', found '{strucId}'");
        var version = r.Int32();
        var length = r.Int32();
        var encoding = r.Int32();
        var ccsid = r.Int32();
        var format = r.String(8);
        var flags = r.Int32();
        var nameValues = length > 32 ? r.String(length - 32) : "";
        var fields = new List<MqProperty>
        {
            new("Name/value data", nameValues, null),
            new("Next format", format, null),
            new("Next CCSID", Ccsids.Describe(ccsid), null),
            new("Flags", flags.ToString(), null),
            new("Version", version.ToString(), null),
        };
        return (new MqHeader(MqFormat.Rfh, "RFH (version 1)", fields, []), (format, ccsid, encoding, length));
    }

    private static (MqHeader, (string, int, int, int)) Transmission(ref MqReader r)
    {
        var strucId = r.String(4);
        if (strucId != "XQH")
            throw new FormatException($"expected StrucId 'XQH ', found '{strucId}'");
        r.Int32(); // version
        var remoteQ = r.String(48);
        var remoteQmgr = r.String(48);
        // Embedded MQMD version 1 (324 bytes): the original message descriptor.
        var mdStart = r.Offset;
        r.String(4); // "MD  "
        r.Int32(); // version
        r.Int32(); // report
        var msgType = r.Int32();
        r.Int32(); // expiry
        r.Int32(); // feedback
        var encoding = r.Int32();
        var ccsid = r.Int32();
        var format = r.String(8);
        var priority = r.Int32();
        var persistence = r.Int32();
        r.Bytes(24); // msg id
        r.Bytes(24); // correl id
        r.Int32(); // backout count
        var replyToQ = r.String(48);
        var replyToQmgr = r.String(48);
        var userId = r.String(12);
        r.Bytes(32); // accounting token
        r.String(32); // appl identity data
        r.Int32(); // put appl type
        var applName = r.String(28);
        var putDate = r.String(8);
        var putTime = r.String(8);
        r.String(4); // appl origin data
        var fields = new List<MqProperty>
        {
            new("Remote queue", $"{remoteQ}@{remoteQmgr}", null),
            new("Original put by", $"{applName} ({userId})", null),
            new("Original put at", FormatTimestamp(putDate, putTime), null),
            new("Original reply-to", replyToQ.Length > 0 ? $"{replyToQ}@{replyToQmgr}" : null, null),
            new("Message type / priority / persistence", $"{msgType} / {priority} / {persistence}", null),
            new("Next format", format, null),
            new("Next CCSID", Ccsids.Describe(ccsid), null),
        };
        return (new MqHeader(MqFormat.Transmission, "Transmission queue header", fields, []), (format, ccsid, encoding, r.Offset));
    }

    /// <summary>MQ put date/time (yyyyMMdd, HHmmsshh in GMT) to ISO 8601.</summary>
    public static string? FormatTimestamp(string date, string time)
    {
        if (date.Length < 8 || time.Length < 6)
            return null;
        var hundredths = time.Length >= 8 ? time[6..8] : "00";
        return $"{date[..4]}-{date[4..6]}-{date[6..8]}T{time[..2]}:{time[2..4]}:{time[4..6]}.{hundredths}Z";
    }
}
