// Index-only GZ pftxs reader; texture bytes stay in the source
using System.Buffers.Binary;
using System.Text;

namespace MgsvModBldr.Tools.Browse;

// Mirrors GzPftxsFile's layout walk (20B header, {nameOffset, ftexSize} index,
// per-entry .ftex bytes + PSUB block of .ftexs) but records each file's region
// instead of copying it — the eager reader also buffers the ENTIRE archive
// before parsing. Same "@"/"/dir/name" naming scheme, same clamp-to-empty for
// out-of-range regions, so listing output is identical.
internal static class LazyGzPftxsReader
{
    internal sealed class Entry
    {
        public string Path = "";
        public long   Offset;
        public int    Size;
    }

    public static List<Entry> Read(Stream s)
    {
        long len = s.Length;
        Span<byte> hdr = stackalloc byte[20];
        ReadAt(s, 0, hdr);
        // "PFTX" + float 1.0 at offset 4 (TPP pftxs has 0x40000000 there).
        if (BinaryPrimitives.ReadUInt32LittleEndian(hdr) != 0x58544650
            || BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(4, 4)) != 0x3F800000)
            throw new InvalidDataException("not a GZ pftxs");
        int fileCount  = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(12, 4));
        int dataOffset = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(16, 4));

        // Entry index: fileCount × { fileNameOffset, ftexSize }.
        var ftexSize = new int[fileCount];
        var names    = new string[fileCount];
        var idx = new byte[fileCount * 8];
        ReadAt(s, 20, idx);
        for (int i = 0; i < fileCount; i++)
        {
            int nameOffset = BinaryPrimitives.ReadInt32LittleEndian(idx.AsSpan(i * 8, 4));
            ftexSize[i]    = BinaryPrimitives.ReadInt32LittleEndian(idx.AsSpan(i * 8 + 4, 4));
            names[i] = NullTermAt(s, nameOffset, len);
        }

        var list = new List<Entry>();
        long pos = dataOffset;
        string dir = "";
        Span<byte> i32 = stackalloc byte[4];
        for (int i = 0; i < fileCount; i++)
        {
            string fn = names[i];
            string nameNoExt;
            if (fn.StartsWith("@"))                 // same directory as the previous entry
                nameNoExt = fn.Substring(1);
            else if (fn.StartsWith("/"))            // "/dir/sub/name" -> dir + name
            {
                string t = fn.Substring(1);
                int slash = t.LastIndexOf('/');
                dir = slash >= 0 ? t.Substring(0, slash) : "";
                nameNoExt = slash >= 0 ? t.Substring(slash + 1) : t;
            }
            else nameNoExt = fn;

            list.Add(new Entry { Path = Combine(dir, nameNoExt + ".ftex"), Offset = pos, Size = Clamp(pos, ftexSize[i], len) });
            pos += ftexSize[i];

            // PSUB: magic | count | count×{offset,size} | align16 | data (align16).
            ReadAt(s, pos + 4, i32);
            int psubCount = BinaryPrimitives.ReadInt32LittleEndian(i32);
            var subSize = new int[psubCount];
            long q = pos + 8;
            var subIdx = new byte[psubCount * 8];
            ReadAt(s, q, subIdx);
            for (int k = 0; k < psubCount; k++)
            {
                subSize[k] = BinaryPrimitives.ReadInt32LittleEndian(subIdx.AsSpan(k * 8 + 4, 4));
                q += 8;
            }
            q = Align16(q);
            for (int k = 0; k < psubCount; k++)
            {
                list.Add(new Entry { Path = Combine(dir, $"{nameNoExt}.{k + 1}.ftexs"), Offset = q, Size = Clamp(q, subSize[k], len) });
                q += subSize[k];
                q = Align16(q);
            }
            pos = q;
        }
        return list;
    }

    // Mirror of the eager reader's Sub() bounds clamp: invalid region -> empty.
    private static int Clamp(long off, int size, long len)
        => size <= 0 || off < 0 || off + size > len ? 0 : size;

    private static long Align16(long v) => (v + 15) & ~15L;

    private static string NullTermAt(Stream s, long off, long len)
    {
        if (off < 0 || off >= len) return "";
        var sb = new List<byte>(64);
        Span<byte> chunk = stackalloc byte[64];
        long pos = off;
        while (pos < len)
        {
            int want = (int)Math.Min(chunk.Length, len - pos);
            ReadAt(s, pos, chunk[..want]);
            for (int i = 0; i < want; i++)
            {
                if (chunk[i] == 0) return Encoding.Latin1.GetString(sb.ToArray());
                sb.Add(chunk[i]);
            }
            pos += want;
        }
        return Encoding.Latin1.GetString(sb.ToArray());
    }

    private static string Combine(string dir, string file)
        => string.IsNullOrEmpty(dir) ? file : dir + "/" + file;

    private static void ReadAt(Stream s, long pos, Span<byte> buf)
    {
        s.Position = pos;
        int n = 0;
        while (n < buf.Length)
        {
            int r = s.Read(buf[n..]);
            if (r == 0) throw new EndOfStreamException();
            n += r;
        }
    }
}
