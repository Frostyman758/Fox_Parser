// Read-only, in-memory FSOP reader for BROWSING — kept separate from FsopPacker
// (which writes .fxc files + a metadata.json and pulls in System.Text.Json and
// the Shift-JIS code-pages provider). FSOP has no file magic: it is a flat
// sequence of entries
//   byte nameLen | name | int32 vsSize | vs | int32 psSize | ps
// where vs/ps are the vertex/pixel shader DXBC blobs, each XOR-0x9C obfuscated.
// Every entry yields a "<name>_vs.fxc" and "<name>_ps.fxc". AOT-clean (no JSON,
// no disk, no code-pages — names are decoded as Latin1 and sanitised).
using System.Buffers.Binary;
using System.Text;

namespace MgsvModBldr.Tools.Fsop;

public sealed class FsopShader
{
    public string Name = "";
    public byte[] Vs = System.Array.Empty<byte>();
    public byte[] Ps = System.Array.Empty<byte>();
}

public sealed class FsopFile
{
    private const byte XorKey = 0x9C;

    public List<FsopShader> Shaders { get; } = new();

    public static FsopFile Read(Stream input)
    {
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length).ToArray());
    }

    private static FsopFile Parse(byte[] data)
    {
        var f = new FsopFile();
        int o = 0;
        while (o < data.Length)
        {
            if (o + 1 > data.Length) break;
            int nameLen = data[o++];
            if (o + nameLen + 4 > data.Length) break;
            string name = DecodeName(data, o, nameLen); o += nameLen;

            int vsSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o, 4)); o += 4;
            if (vsSize < 0 || o + vsSize + 4 > data.Length) break;
            byte[] vs = Xor(data, o, vsSize); o += vsSize;

            int psSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o, 4)); o += 4;
            if (psSize < 0 || o + psSize > data.Length) break;
            byte[] ps = Xor(data, o, psSize); o += psSize;

            f.Shaders.Add(new FsopShader { Name = name, Vs = vs, Ps = ps });
        }
        return f;
    }

    // Strict structural probe used for extension-less detection: the byte stream
    // must parse cleanly as >=1 entry and consume EXACTLY to EOF. Random data is
    // extremely unlikely to satisfy this, so it doubles as a content check.
    public static bool LooksLikeFsop(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10) return false;
        int o = 0, entries = 0;
        while (o < data.Length)
        {
            if (o + 1 > data.Length) return false;
            int nameLen = data[o++];
            if (nameLen == 0 || o + nameLen + 4 > data.Length) return false;
            o += nameLen;
            int vsSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(o, 4)); o += 4;
            if (vsSize < 0 || o + vsSize + 4 > data.Length) return false;
            o += vsSize;
            int psSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(o, 4)); o += 4;
            if (psSize < 0 || o + psSize > data.Length) return false;
            o += psSize;
            if (++entries > 100000) return false;
        }
        return o == data.Length && entries > 0;
    }

    private static byte[] Xor(byte[] src, int off, int len)
    {
        var r = new byte[len];
        for (int i = 0; i < len; i++) r[i] = (byte)(src[off + i] ^ XorKey);
        return r;
    }

    private static string DecodeName(byte[] data, int off, int len)
    {
        // Names are short shader identifiers; Latin1 is lossless + AOT-safe.
        var s = Encoding.Latin1.GetString(data, off, len).TrimEnd('\0').Trim();
        foreach (var c in new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })
            s = s.Replace(c, '_');
        return s.Length == 0 ? "unnamed" : s;
    }
}
