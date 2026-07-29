// Index-only stp/sab readers; payload bytes stay on disk
using System.Buffers.Binary;

namespace MgsvModBldr.Tools.Browse;

// Mirrors StreamedPackage/StreamedAnimation but records each blob's region
// instead of loading it. Block sizes come from offset deltas (the last block
// runs to EOF), exactly as the eager readers compute them. All payloads are
// raw (wem/ls2/lsst — no crypto). Byte-for-byte identical — verified by the
// lazy-vs-eager test.
internal static class LazyStpReader
{
    // .stp entry: a .wem (always) and, on TPP, a .ls2 lipsync (Ls2Size<0 = none).
    internal sealed class Entry
    {
        public uint Name;
        public long WemOffset; public int WemSize;
        public long Ls2Offset; public int Ls2Size = -1;
    }

    public static List<Entry> Read(Stream s)
    {
        long len = s.Length;
        Span<byte> head = stackalloc byte[8];
        ReadExact(s, head);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != 0x4C505453)   // 'STPL'
            throw new InvalidDataException("not an stp");
        int count   = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(4, 2));
        int version = head[6];                                              // 0=GZ, 1=TPP
        bool tpp    = version == 1;
        if (version != 0 && version != 1) throw new InvalidDataException("unknown stp version");

        var names  = new uint[count];
        var wemOff = new int[count];
        var ls2Off = new int[count];
        int entrySize = tpp ? 12 : 8;
        Span<byte> e = stackalloc byte[12];
        for (int i = 0; i < count; i++)
        {
            ReadExact(s, e.Slice(0, entrySize));
            names[i]  = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(0, 4));
            wemOff[i] = BinaryPrimitives.ReadInt32LittleEndian(e.Slice(4, 4));
            if (tpp) ls2Off[i] = BinaryPrimitives.ReadInt32LittleEndian(e.Slice(8, 4));
        }

        var list = new List<Entry>(count);
        for (int i = 0; i < count; i++)
        {
            var en = new Entry { Name = names[i] };
            if (!tpp)
            {
                int wemSize = (i < count - 1 ? wemOff[i + 1] : (int)len) - wemOff[i];
                en.WemOffset = wemOff[i]; en.WemSize = wemSize;
            }
            else
            {
                int ls2Size = wemOff[i] - ls2Off[i];
                int wemSize = (i < count - 1 ? ls2Off[i + 1] : (int)len) - wemOff[i];
                en.Ls2Offset = ls2Off[i]; en.Ls2Size = ls2Size;
                en.WemOffset = wemOff[i]; en.WemSize = wemSize;
            }
            list.Add(en);
        }
        return list;
    }

    private static void ReadExact(Stream s, Span<byte> buf)
    {
        int n = 0;
        while (n < buf.Length) { int r = s.Read(buf[n..]); if (r == 0) throw new EndOfStreamException(); n += r; }
    }
}

// Index-only reader for .sab (StreamedAnimation): a 64-bit hash + one .lsst block.
internal static class LazySabReader
{
    internal sealed class Entry { public ulong Name; public long Offset; public int Size; }

    public static List<Entry> Read(Stream s)
    {
        long len = s.Length;
        Span<byte> head = stackalloc byte[8];
        ReadExact(s, head);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != 0x334C4153)   // 'SAL3'
            throw new InvalidDataException("not a sab");
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(4, 4));

        var names = new ulong[count];
        var off   = new int[count];
        Span<byte> e = stackalloc byte[16];
        for (int i = 0; i < count; i++)
        {
            ReadExact(s, e);
            names[i] = BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(0, 8));
            off[i]   = BinaryPrimitives.ReadInt32LittleEndian(e.Slice(8, 4));
        }

        var list = new List<Entry>(count);
        for (int i = 0; i < count; i++)
        {
            int size = (i < count - 1 ? off[i + 1] : (int)len) - off[i];
            list.Add(new Entry { Name = names[i], Offset = off[i], Size = size });
        }
        return list;
    }

    private static void ReadExact(Stream s, Span<byte> buf)
    {
        int n = 0;
        while (n < buf.Length) { int r = s.Read(buf[n..]); if (r == 0) throw new EndOfStreamException(); n += r; }
    }
}
