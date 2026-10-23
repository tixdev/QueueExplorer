using System.Text;

namespace DynaQueueExplorer.IbmMq.Formats;

/// <summary>Coded character set ids (CCSIDs) used by IBM MQ, mapped to .NET encodings.</summary>
public static class Ccsids
{
    static Ccsids() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly Dictionary<int, (int CodePage, string Label)> Known = new()
    {
        [1208] = (65001, "UTF-8"),
        [819] = (28591, "ISO 8859-1 (Latin-1)"),
        [923] = (28605, "ISO 8859-15 (Latin-9)"),
        [1252] = (1252, "Windows Latin-1"),
        [5348] = (1252, "Windows Latin-1 (euro)"),
        [437] = (437, "PC US"),
        [850] = (850, "PC Latin-1"),
        [858] = (858, "PC Latin-1 (euro)"),
        [1200] = (1201, "UTF-16 big-endian"),
        [13488] = (1201, "UCS-2 big-endian"),
        [17584] = (1201, "UCS-2 big-endian"),
        [1202] = (1200, "UTF-16 little-endian"),
        [37] = (37, "EBCDIC US/Canada"),
        [500] = (500, "EBCDIC International"),
        [1047] = (1047, "EBCDIC Latin-1 (open systems)"),
        [1140] = (1140, "EBCDIC US/Canada (euro)"),
        [1141] = (1141, "EBCDIC Germany (euro)"),
        [1144] = (1144, "EBCDIC Italy (euro)"),
        [1145] = (1145, "EBCDIC Spain (euro)"),
        [1146] = (1146, "EBCDIC UK (euro)"),
        [1147] = (1147, "EBCDIC France (euro)"),
        [1148] = (1148, "EBCDIC International (euro)"),
        [273] = (20273, "EBCDIC Germany"),
        [277] = (20277, "EBCDIC Denmark/Norway"),
        [278] = (20278, "EBCDIC Finland/Sweden"),
        [280] = (20280, "EBCDIC Italy"),
        [284] = (20284, "EBCDIC Spain"),
        [285] = (20285, "EBCDIC UK"),
        [297] = (20297, "EBCDIC France"),
    };

    public static bool IsEbcdic(int ccsid) => Known.TryGetValue(ccsid, out var k) && k.Label.StartsWith("EBCDIC", StringComparison.Ordinal);

    /// <summary>The encoding for a CCSID, or null when it is unknown (the body is then shown as binary).</summary>
    public static Encoding? GetEncoding(int ccsid)
    {
        if (!Known.TryGetValue(ccsid, out var known))
            return null;
        try
        {
            return Encoding.GetEncoding(known.CodePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static string Describe(int ccsid) => Known.TryGetValue(ccsid, out var k) ? $"{ccsid} · {k.Label}" : $"{ccsid}";
}
