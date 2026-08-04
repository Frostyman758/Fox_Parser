// Read an mtar animation table without its ganis
namespace MgsvModBldr.Tools.Index;

/// <summary>
/// Lists the animations in a .mtar (v2/GANI2) without reading a gani.
/// Front-loaded like an fpk: 0x20 header (fileCount at +4, trackOffset at +0x14)
/// then fileCount x 0x20 entries, then the track/chunk data and the gani blobs.
/// Index size is therefore 0x20 + count * 0x20, known from the header alone.
///
/// Takes a byte source rather than a QAR entry: an .mtar is almost never a
/// top-level dat entry — it sits inside an .fpk — so the caller supplies a reader
/// for whatever range it lives in (a QAR entry range, an fpk slice, a file).
/// v1/GZ mtars use a different layout and are rejected.
/// </summary>
public static class MtarIndex
{
    private const int FileHeader = 0x20, EntrySize = 0x20;
    private const uint V1Marker = 0x0BFCA2D2u;   // first blob of an old-format mtar

    /// <summary>One animation slot. Size is in 16-byte units on disk, expanded here.</summary>
    public sealed record Gani(ulong Hash, uint Offset, int Size, bool HasMotionPoints);


    public static List<Gani> Read(RangeReader read, long totalSize, out int bytesRead)
    {
        bytesRead = 0;
        if (totalSize < FileHeader) return null;

        var head = read(0, FileHeader);
        if (head is null || head.Length < FileHeader) return null;
        bytesRead += head.Length;

        uint count = BitConverter.ToUInt32(head, 4);
        if (count == 0 || count > 1_000_000) return null;

        long tableEnd = FileHeader + (long)count * EntrySize;
        if (tableEnd > totalSize) return null;

        var table = read(FileHeader, (int)(tableEnd - FileHeader));
        if (table is null || table.Length < tableEnd - FileHeader) return null;
        bytesRead += table.Length;

        // An old-format (v1/GZ) mtar starts its first blob with a known marker and
        // does not use this table layout. The first entry's offset field is at 0x28
        // absolute — i.e. +8 inside entry 0, which is the start of `table`.
        uint firstOff = BitConverter.ToUInt32(table, 8);
        if (firstOff >= tableEnd && firstOff + 4 <= totalSize)
        {
            var probe = read(firstOff, 4);
            if (probe is { Length: 4 })
            {
                bytesRead += probe.Length;
                if (BitConverter.ToUInt32(probe, 0) == V1Marker) return null;
            }
        }

        var list = new List<Gani>((int)count);
        for (int i = 0; i < count; i++)
        {
            int at = i * EntrySize;
            list.Add(new Gani(
                BitConverter.ToUInt64(table, at),
                BitConverter.ToUInt32(table, at + 8),
                BitConverter.ToUInt16(table, at + 12) * 16,
                BitConverter.ToInt16(table, at + 0x10) != 0));
        }
        return list;
    }
}
