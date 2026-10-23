using System.Text;
using System.Xml;

namespace DynaQueueExplorer.Msmq;

public static class MsmqFormats
{
    /// <summary>
    /// NServiceBus on MSMQ stores its headers in the extension property as XML:
    /// <c>&lt;ArrayOfHeaderInfo&gt;&lt;HeaderInfo&gt;&lt;Key/&gt;&lt;Value/&gt;&lt;/HeaderInfo&gt;…</c>. Null when the extension is something else.
    /// </summary>
    public static Dictionary<string, string?>? ParseNServiceBusHeaders(byte[] extension)
    {
        if (extension.Length == 0)
            return null;
        var text = DecodeText(extension);
        if (text is null || !text.Contains("HeaderInfo", StringComparison.Ordinal))
            return null;
        try
        {
            var headers = new Dictionary<string, string?>(StringComparer.Ordinal);
            // No DTDs, no external entities: the extension is data from any sender.
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = true,
                IgnoreComments = true,
            });
            string? key = null, value = null;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "HeaderInfo")
                {
                    key = value = null;
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Key")
                {
                    key = reader.ReadElementContentAsString();
                    if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Value")
                        value = ReadValue(reader);
                    if (key.Length > 0)
                        headers[key] = value;
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Value" && key is not null)
                {
                    headers[key] = ReadValue(reader);
                }
            }
            return headers.Count > 0 ? headers : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>xsi:nil values are null, everything else its text.</summary>
    private static string? ReadValue(XmlReader reader)
    {
        var nil = reader.GetAttribute("nil", "http://www.w3.org/2001/XMLSchema-instance") == "true";
        var value = reader.ReadElementContentAsString();
        return nil ? null : value;
    }

    /// <summary>UTF-8 (with or without BOM) or UTF-16 text; null for binary data.</summary>
    public static string? DecodeText(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 2 && data[1] == 0 && data[0] == (byte)'<')
            return Encoding.Unicode.GetString(data);
        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data).TrimStart('﻿').TrimEnd('\0');
            return text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t') ? null : text;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Meaning of the message class (MQMSG_CLASS_*): for messages in a dead-letter queue it says why they are there.
    /// </summary>
    public static string? DescribeClass(int? messageClass) => messageClass switch
    {
        null => null,
        0x0000 => "normal message",
        0x0001 => "report message",
        0x0002 => "acknowledgement: reached the queue",
        0x4000 => "acknowledgement: received",
        0x8000 => "NACK: bad destination queue (the queue does not exist or the format name is invalid)",
        0x8001 => "NACK: purged before reaching the queue",
        0x8002 => "NACK: time to reach queue expired",
        0x8003 => "NACK: destination queue quota exceeded",
        0x8004 => "NACK: access denied to the destination queue",
        0x8005 => "NACK: hop count exceeded",
        0x8006 => "NACK: bad signature",
        0x8007 => "NACK: could not be decrypted",
        0x8008 => "NACK: could not be encrypted",
        0x8009 => "NACK: transactional message sent to a non-transactional queue",
        0x800A => "NACK: non-transactional message sent to a transactional queue",
        0x800B => "NACK: unsupported cryptographic provider",
        0x800C => "NACK: source computer GUID changed",
        0xC000 => "NACK: destination queue deleted before the message was read",
        0xC001 => "NACK: purged from the destination queue",
        0xC002 => "NACK: time to be received expired in the destination queue",
        0xC003 => "NACK: time to be received expired (detected at the sender)",
        var c => $"class 0x{c:X4}",
    };
}
