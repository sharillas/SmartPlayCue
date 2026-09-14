using System.IO;
using System.Text;

namespace StagePlayout.Core.Services;

/// <summary>
/// OSC (Open Sound Control) mínimo e próprio — sem dependências externas.
/// Mensagens UDP (endereço + type tags + argumentos) e bundles básicos.
/// </summary>
public static class OscUdp
{
    public static byte[] Pack(string address, params object?[] args)
    {
        using var ms = new MemoryStream();
        WritePadded(ms, Encoding.UTF8.GetBytes(address));

        var tags = new StringBuilder(",");
        foreach (var a in args)
        {
            tags.Append(a switch
            {
                int or long or short or byte => 'i',
                float or double or decimal => 'f',
                string => 's',
                bool b => b ? 'T' : 'F',
                _ => 's',
            });
        }
        WritePadded(ms, Encoding.UTF8.GetBytes(tags.ToString()));

        foreach (var a in args)
        {
            switch (a)
            {
                case int i: WriteInt32(ms, i); break;
                case long l: WriteInt32(ms, (int)l); break;
                case float f: WriteFloat(ms, f); break;
                case double d: WriteFloat(ms, (float)d); break;
                case decimal d: WriteFloat(ms, (float)d); break;
                case string s: WritePadded(ms, Encoding.UTF8.GetBytes(s)); break;
                case bool b: ms.WriteByte(b ? (byte)1 : (byte)0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); break;
            }
        }
        return ms.ToArray();
    }

    private static void WritePadded(MemoryStream ms, byte[] data)
    {
        ms.Write(data, 0, data.Length);
        // OSC: string tem SEMPRE pelo menos um \0 terminador, depois padding até 4
        var nulls = 4 - (data.Length % 4);
        for (var i = 0; i < nulls; i++) ms.WriteByte(0);
    }

    private static void WriteInt32(MemoryStream ms, int v)
    {
        ms.WriteByte((byte)(v >> 24));
        ms.WriteByte((byte)(v >> 16));
        ms.WriteByte((byte)(v >> 8));
        ms.WriteByte((byte)v);
    }

    private static void WriteFloat(MemoryStream ms, float v)
    {
        var b = BitConverter.GetBytes(v);
        if (BitConverter.IsLittleEndian) Array.Reverse(b);
        ms.Write(b, 0, 4);
    }

    /// <summary>Extrai todas as mensagens de um datagrama (inclui elementos de bundle).</summary>
    public static IEnumerable<(string Address, object?[] Args)> Unpack(byte[] data)
    {
        if (data.Length >= 16 && Encoding.ASCII.GetString(data, 0, 8) == "#bundle\0")
        {
            var offset = 16; // "#bundle\0" (8) + timetag (8)
            while (offset + 4 <= data.Length)
            {
                var size = (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
                offset += 4;
                if (size <= 0 || offset + size > data.Length) break;
                foreach (var m in UnpackElement(data, offset, size))
                    yield return m;
                offset += size;
            }
            yield break;
        }

        foreach (var m in UnpackElement(data, 0, data.Length))
            yield return m;
    }

    private static IEnumerable<(string Address, object?[] Args)> UnpackElement(byte[] data, int start, int length)
    {
        var end = start + length;
        var addrEnd = FindNull(data, start, end);
        if (addrEnd < 0) yield break;
        var address = Encoding.UTF8.GetString(data, start, addrEnd - start);

        var tagStart = Align4(addrEnd + 1); // tag começa no próximo múltiplo de 4
        var tagEnd = FindNull(data, tagStart, end);
        if (tagEnd < 0 || tagEnd == tagStart || data[tagStart] != (byte)',')
        {
            yield return (address, Array.Empty<object?>());
            yield break;
        }
        var tags = Encoding.UTF8.GetString(data, tagStart, tagEnd - tagStart);
        var pos = Align4(tagEnd + 1);

        var args = new List<object?>();
        for (var i = 1; i < tags.Length; i++)
        {
            switch (tags[i])
            {
                case 'i' when pos + 4 <= end:
                    args.Add(ReadInt32(data, pos)); pos += 4; break;
                case 'f' when pos + 4 <= end:
                    args.Add(ReadFloat(data, pos)); pos += 4; break;
                case 's':
                    var sEnd = FindNull(data, pos, end);
                    if (sEnd < 0) { args.Add(""); pos = end; }
                    else { args.Add(Encoding.UTF8.GetString(data, pos, sEnd - pos)); pos = Align4(sEnd + 1); }
                    break;
                case 'T': args.Add(true); break;
                case 'F': args.Add(false); break;
                default:
                    break; // tipos não suportados são ignorados
            }
        }
        yield return (address, args.ToArray());
    }

    private static int FindNull(byte[] data, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (data[i] == 0) return i;
        return -1;
    }

    private static int Align4(int v) => (v + 3) & ~3;

    private static int ReadInt32(byte[] b, int o)
        => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    private static float ReadFloat(byte[] b, int o)
        => BitConverter.ToSingle(new[] { b[o + 3], b[o + 2], b[o + 1], b[o] }, 0);
}
