// Index-only sbp reader; sub-file bytes stay on disk
using System.Buffers.Binary;
using System.Text;

namespace MgsvModBldr.Tools.Browse;

// Mirrors SbpFile but records each sub-file's region instead of loading it.
// SBP blocks are raw (bnk/stp/sab payloads, no crypto), so on-demand decode is
// a copy. Byte-for-byte identical to SbpFile — verified by the test.
//
// Layout (little-endian): 'SBPL' | byte fileCount | uint16 headerSize | pad(0)
//   then fileCount × { char[4] magic | uint32 offset | int32 size }
//   then 16-aligned data blocks. We keep the magic (for "<i>.<tag>" naming and
//   nested-container detection) and the exact offset/size region.
internal static class LazySbpReader
{
    internal sealed class Entry
    {
        public string Magic = "";
        public long   DataOffset;
        public int    DataSize;
    }

    public static List<Entry> Read(Stream s)
    {
        Span<byte> head = stackalloc byte[8];
        ReadExact(s, head);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != 0x4C504253)   // 'SBPL'
            throw new InvalidDataException("not an sbp");
        int fileCount = head[4];

        var list = new List<Entry>(fileCount);
        Span<byte> e = stackalloc byte[12];
        for (int i = 0; i < fileCount; i++)
        {
            s.Position = 8 + (long)i * 12;
            ReadExact(s, e);
            list.Add(new Entry
            {
                Magic      = Encoding.ASCII.GetString(e.Slice(0, 4)).TrimEnd('\0'),
                DataOffset = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(4, 4)),
                DataSize   = BinaryPrimitives.ReadInt32LittleEndian(e.Slice(8, 4)),
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
