// Index-only fsop reader; shader blobs stay on disk
using System.Buffers.Binary;
using System.Text;

namespace MgsvModBldr.Tools.Browse;

// Mirrors FsopFile's structure walk but records each shader blob's region
// instead of XOR-decoding it up front. The vs/ps DXBC blobs are XOR-0x9C
// obfuscated, so the on-demand decode is LazyBlob.Xor9C. Byte-for-byte
// identical to FsopFile — verified by the test.
//
// Layout: a flat sequence of entries, each
//   byte nameLen | name(nameLen) | int32 vsSize | vs(vsSize) | int32 psSize | ps(psSize)
internal static class LazyFsopReader
{
    internal sealed class Entry
    {
        public string Name = "";
        public long VsOffset; public int VsSize;
        public long PsOffset; public int PsSize;
    }

    public static List<Entry> Read(Stream s)
    {
        // FSOP has no magic and sizes are interleaved, so we must walk the whole
        // structure — but we read only the small name+size fields, seeking PAST
        // the (often large) shader blobs instead of loading them.
        long len = s.Length;
        var list = new List<Entry>();
        long o = 0;
        Span<byte> i32 = stackalloc byte[4];
        while (o < len)
        {
            s.Position = o;
            int nameLen = s.ReadByte();
            if (nameLen < 0) break;
            o += 1;
            if (o + nameLen + 4 > len) break;
            var nameBuf = new byte[nameLen];
            ReadExact(s, nameBuf);
            o += nameLen;
            string name = DecodeName(nameBuf);

            ReadExact(s, i32); int vsSize = BinaryPrimitives.ReadInt32LittleEndian(i32); o += 4;
            if (vsSize < 0 || o + vsSize + 4 > len) break;
            long vsOff = o; o += vsSize;

            s.Position = o;
            ReadExact(s, i32); int psSize = BinaryPrimitives.ReadInt32LittleEndian(i32); o += 4;
            if (psSize < 0 || o + psSize > len) break;
            long psOff = o; o += psSize;

            list.Add(new Entry { Name = name, VsOffset = vsOff, VsSize = vsSize, PsOffset = psOff, PsSize = psSize });
        }
        return list;
    }

    private static string DecodeName(byte[] data)
    {
        var s = Encoding.Latin1.GetString(data).TrimEnd('\0').Trim();
        foreach (var c in new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })
            s = s.Replace(c, '_');
        return s.Length == 0 ? "unnamed" : s;
    }

    private static void ReadExact(Stream s, Span<byte> buf)
    {
        int n = 0;
        while (n < buf.Length) { int r = s.Read(buf[n..]); if (r == 0) throw new EndOfStreamException(); n += r; }
    }
}
