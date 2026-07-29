// Index-only fpk reader; entry bytes stay on disk
using System.Buffers.Binary;
using System.Text;

namespace MgsvModBldr.Tools.Browse;

// Unlike FpkFile (which loads every entry's bytes), this reads JUST the header
// + entry records + path strings. ArchiveHandle pulls a file only when opened
// (see ReadFile + the FpkCrypto decode there). Byte-for-byte identical output
// to FpkFile — verified by the lazy-vs-eager round-trip test.
internal static class LazyFpkReader
{
    internal sealed class Entry
    {
        public string Path = "";
        public long   DataOffset;
        public int    DataSize;
    }

    public static List<Entry> Read(Stream s)
    {
        Span<byte> hdr = stackalloc byte[48];
        ReadExact(s, hdr);
        if (!(hdr[0] == 0x66 && hdr[1] == 0x6f && hdr[2] == 0x78 && hdr[3] == 0x66 &&
              hdr[4] == 0x70 && hdr[5] == 0x6b))                 // "foxfpk"
            throw new InvalidDataException("not an fpk");
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(36, 4));

        // Each 48-byte record: dataOffset@0, dataSize@8, strOffset@16, strLen@24,
        // md5@32. Read the records first (sequential), then the path strings.
        var dataOff = new long[entryCount];
        var dataSz  = new int[entryCount];
        var strOff  = new long[entryCount];
        var strLen  = new int[entryCount];
        var rec = new byte[48];
        for (int i = 0; i < entryCount; i++)
        {
            s.Position = 48L + i * 48L;
            ReadExact(s, rec);
            dataOff[i] = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(0, 4));
            dataSz[i]  = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(8, 4));
            strOff[i]  = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(16, 4));
            strLen[i]  = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(24, 4));
        }

        var list = new List<Entry>((int)entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            s.Position = strOff[i];
            var sb = new byte[strLen[i]];
            ReadExact(s, sb);
            list.Add(new Entry { Path = Encoding.UTF8.GetString(sb), DataOffset = dataOff[i], DataSize = dataSz[i] });
        }
        return list;
    }

    private static void ReadExact(Stream s, Span<byte> buf)
    {
        int n = 0;
        while (n < buf.Length) { int r = s.Read(buf[n..]); if (r == 0) throw new EndOfStreamException(); n += r; }
    }
}
