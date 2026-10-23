using System.Text;

namespace DynaQueueExplorer.IbmMq.Formats;

public sealed record PcfParameter(int Id, string Name, string Value);

/// <summary>A decoded PCF message (command, response or event): the MQCFH header and its parameters.</summary>
public sealed record PcfMessage(int Type, int Command, string CommandName, int CompCode, int Reason, string ReasonName, bool Last, IReadOnlyList<PcfParameter> Parameters);

/// <summary>Decodes PCF bodies (formats MQADMIN, MQPCF, MQEVENT) into a parameter table.</summary>
public static class Pcf
{
    private const int CfhLength = 36;

    public static PcfMessage Decode(ReadOnlySpan<byte> data, int encoding)
    {
        var r = new MqReader(data, MqHeaderChain.IsBigEndian(encoding), Encoding.ASCII);
        var type = r.Int32();
        var strucLength = r.Int32();
        if (strucLength != CfhLength)
            throw new FormatException($"MQCFH length {strucLength}");
        r.Int32(); // version
        var command = r.Int32();
        r.Int32(); // sequence number
        var control = r.Int32();
        var compCode = r.Int32();
        var reason = r.Int32();
        var count = r.Int32();
        var parameters = new List<PcfParameter>();
        ReadParameters(data, ref r, count, "", parameters);
        return new PcfMessage(type, command, MqConstants.Command(command) ?? $"command {command}", compCode, reason,
            MqReasons.Describe(reason), control == 1, parameters);
    }

    private static void ReadParameters(ReadOnlySpan<byte> data, ref MqReader r, int count, string indent, List<PcfParameter> output)
    {
        for (var i = 0; i < count && r.Remaining >= 8; i++)
        {
            var start = r.Offset;
            var type = r.Int32();
            var length = r.Int32();
            var id = r.Int32();
            switch (type)
            {
                case 3: // MQCFIN
                    output.Add(Param(id, indent, r.Int32().ToString(), isString: false));
                    break;
                case 23: // MQCFIN64
                    r.Int32();
                    output.Add(Param(id, indent, r.Int64().ToString(), isString: false));
                    break;
                case 4: // MQCFST
                {
                    var ccsid = r.Int32();
                    var len = r.Int32();
                    var text = Ccsids.GetEncoding(ccsid) ?? Encoding.ASCII;
                    output.Add(Param(id, indent, text.GetString(data.Slice(r.Offset, len)).TrimEnd(' ', '\0'), isString: true));
                    break;
                }
                case 5: // MQCFIL
                {
                    var n = r.Int32();
                    var values = Enumerable.Range(0, n).Select(_ => 0).ToArray();
                    for (var k = 0; k < n; k++) values[k] = r.Int32();
                    output.Add(Param(id, indent, string.Join(", ", values), isString: false));
                    break;
                }
                case 6: // MQCFSL
                {
                    var ccsid = r.Int32();
                    var n = r.Int32();
                    var len = r.Int32();
                    var text = Ccsids.GetEncoding(ccsid) ?? Encoding.ASCII;
                    var values = new List<string>();
                    for (var k = 0; k < n; k++)
                        values.Add(text.GetString(data.Slice(r.Offset + k * len, len)).TrimEnd(' ', '\0'));
                    output.Add(Param(id, indent, string.Join(", ", values), isString: true));
                    break;
                }
                case 9: // MQCFBS
                {
                    var len = r.Int32();
                    output.Add(Param(id, indent, Convert.ToHexString(data.Slice(r.Offset, len)), isString: true));
                    break;
                }
                case 20: // MQCFGR: a group of parameters
                {
                    var n = r.Int32();
                    output.Add(Param(id, indent, $"group of {n}", isString: false));
                    r.Offset = start + length;
                    ReadParameters(data, ref r, n, indent + "  ", output);
                    continue;
                }
                default:
                    output.Add(new PcfParameter(id, $"{indent}parameter {id}", $"(type {type}, {length} bytes)"));
                    break;
            }
            r.Offset = start + length;
        }
    }

    private static PcfParameter Param(int id, string indent, string value, bool isString) =>
        new(id, indent + (MqConstants.Parameter(id, isString) ?? $"parameter {id}"), value);
}
