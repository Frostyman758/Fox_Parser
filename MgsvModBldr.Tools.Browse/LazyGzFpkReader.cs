// Index-only GZ fpk reader; entry bytes stay in the source
using System.Buffers.Binary;
using MgsvModBldr.Tools.Fpk.Gz;

namespace MgsvModBldr.Tools.Browse;

// Mirrors GzFpkFile/GzFpkEntry but records each entry's region instead of
// copying its bytes (the eager reader duplicates the whole archive next to the
// parent blob). Names resolve through the SAME GzFpkString (string heap + MD5 +
// fpk_dictionary), so listing output is identical. GZ fpk data is plaintext —
// on-demand decode is a raw copy.
internal static class LazyGzFpkReader
{
    internal sealed class Entry
    {
        public string Path = "";
        public long   DataOffset;
        public int    DataSize;
    }

    public static List<Entry> Read(Stream r)
    {
        Span<byte> hdr = stackalloc byte[GzFpkFile.HeaderSize];
        ReadExact(r,hdr);
        if (!GzFpkFile.IsGzMagic(hdr))
            throw new InvalidDataException("not a GZ (ste) fpk");
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(36, 4));

        // One 48-byte record per entry: data offset/size (16B) + GzFpkString
        // header (16B, seeks to the string heap) + path MD5 (16B).
        var list = new List<Entry>((int)entryCount);
        Span<byte> info = stackalloc byte[16];
        Span<byte> md5 = stackalloc byte[16];
        for (int i = 0; i < entryCount; i++)
        {
            ReadExact(r,info);
            uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(info.Slice(0, 4));
            int  dataSize   = BinaryPrimitives.ReadInt32LittleEndian(info.Slice(8, 4));

            var name = new GzFpkString();
            name.Read(r);
            ReadExact(r,md5);
            name.Resolve(md5.ToArray());

            list.Add(new Entry
            {
                Path = name.Path,
                DataOffset = dataOffset,
                DataSize = dataSize < 0 ? 0 : dataSize,
            });
        }
        return list;
    }

    private static void ReadExact(Stream s, Span<byte> buf)
    {
        int n = 0;
        while (n < buf.Length) { int r = s.Read(buf[n..]); if (r == 0) throw new EndOfStreamException(); n += r; }
    }
}
