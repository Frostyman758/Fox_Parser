// Index-only pftxs reader; texture bytes stay on disk
using System.Buffers.Binary;

namespace MgsvModBldr.Tools.Browse;

// Mirrors PftxsFile's layout walk but records each entry's absolute file region
// instead of loading its bytes. PFTX entry data is raw (ftex/ftexs, no crypto),
// so the on-demand decode is a straight copy. Byte-for-byte identical to
// PftxsFile — verified by the lazy-vs-eager round-trip test.
//
// Layout (all little-endian):
//   file:  32B header — "PFTX"@0, "TEXL"@16, fileCount@24
//   group: 32B header — "FTEX"@0, groupHash@8, entryCount@16, at basePos
//          entryCount × 16B entry header — hash@0, offset@8, size@12
//          entry data at basePos + offset, length size
//   Groups are contiguous; the next group starts exactly where the last entry
//   of the current group ends (matches PftxsGroup.Read/Write).
internal static class LazyPftxsReader
{
    internal sealed class Entry
    {
        public ulong Hash;
        public long  DataOffset;
        public int   DataSize;
    }

    public static List<Entry> Read(Stream s)
    {
        Span<byte> fh = stackalloc byte[32];
        ReadExact(s, fh);
        if (!(fh[0] == (byte)'P' && fh[1] == (byte)'F' && fh[2] == (byte)'T' && fh[3] == (byte)'X'))
            throw new InvalidDataException("not a pftxs");
        int groupCount = BinaryPrimitives.ReadInt32LittleEndian(fh.Slice(24, 4));

        var list = new List<Entry>();
        long basePos = s.Position;            // first group starts right after the 32B file header
        Span<byte> gh = stackalloc byte[32];
        Span<byte> eh = stackalloc byte[16];

        for (int g = 0; g < groupCount; g++)
        {
            s.Position = basePos;
            ReadExact(s, gh);                 // "FTEX" + groupHash + entryCount
            int count = BinaryPrimitives.ReadInt32LittleEndian(gh.Slice(16, 4));

            long lastOff = 32, lastSize = 0;  // empty group ends at basePos + 32
            // entry headers are contiguous right after the group header
            for (int i = 0; i < count; i++)
            {
                s.Position = basePos + 32 + (long)i * 16;
                ReadExact(s, eh);
                ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(eh.Slice(0, 8));
                int off    = BinaryPrimitives.ReadInt32LittleEndian(eh.Slice(8, 4));
                int size   = BinaryPrimitives.ReadInt32LittleEndian(eh.Slice(12, 4));
                list.Add(new Entry { Hash = hash, DataOffset = basePos + off, DataSize = size });
                lastOff = off; lastSize = size;   // entries are in ascending-offset order
            }

            basePos = basePos + lastOff + lastSize;   // start of the next group
        }
        return list;
    }

    private static void ReadExact(Stream s, Span<byte> buf)
    {
        int n = 0;
        while (n < buf.Length) { int r = s.Read(buf[n..]); if (r == 0) throw new EndOfStreamException(); n += r; }
    }
}
