// fpk/fpkd file list without the payload
namespace MgsvModBldr.Tools.Index;

/// <summary>
/// Lists an .fpk/.fpkd's contents without reading a single file from it.
/// Header, entry table, reference table and path strings all sit before the file
/// data, so the index is a bounded prefix — sized exactly from the header's table
/// counts rather than guessed at.
///
/// The tables are walked here rather than through Tools.Fpk's FpkFile, because
/// FpkEntry.Read calls ReadData: it seeks to every entry's DataOffset and pulls
/// the whole payload, so that reader always needs the entire pack.
/// </summary>
public static class FpkIndex
{
    private const int Header = 48, EntrySize = 48, StrHeader = 16;

    public sealed record Entry(string Path, uint DataOffset, uint DataSize);

    /// <summary>Bytes the index occupies, or 0 if this isn't a readable fpk.</summary>
    public static int IndexSize(RangeReader read, long totalSize)
    {
        var head = read(0, Header);
        if (head is null || head.Length < Header) return 0;
        if (!ContainerKind.IsPack(ContainerKind.Detect(head))) return 0;

        uint entryCount = BitConverter.ToUInt32(head, 36);
        uint refCount = BitConverter.ToUInt32(head, 40);
        if (entryCount > 1_000_000 || refCount > 1_000_000) return 0;

        long tablesEnd = Header + (long)entryCount * EntrySize + (long)refCount * StrHeader;
        if (tablesEnd <= 0 || tablesEnd > totalSize) return 0;

        var tables = read(Header, (int)(tablesEnd - Header));
        if (tables is null || tables.Length < tablesEnd - Header) return 0;

        // Path strings live after the tables, addressed absolutely; the index ends
        // at the furthest one.
        long end = tablesEnd;
        for (int i = 0; i < entryCount; i++)
            end = Math.Max(end, StringEnd(tables, i * EntrySize + 16));
        long refBase = (long)entryCount * EntrySize;
        for (int i = 0; i < refCount; i++)
            end = Math.Max(end, StringEnd(tables, (int)refBase + i * StrHeader));

        return end > totalSize ? 0 : (int)end;
    }

    public static List<Entry> Read(RangeReader read, long totalSize, out int bytesRead)
    {
        bytesRead = 0;
        int size = IndexSize(read, totalSize);
        if (size <= 0) return null;

        var b = read(0, size);
        if (b is null || b.Length < size) return null;
        bytesRead = b.Length;

        uint entryCount = BitConverter.ToUInt32(b, 36);
        var list = new List<Entry>((int)Math.Min(entryCount, 100_000));
        for (int i = 0; i < entryCount; i++)
        {
            int at = Header + i * EntrySize;
            if (at + EntrySize > b.Length) return null;
            uint strOff = BitConverter.ToUInt32(b, at + 16);
            uint strLen = BitConverter.ToUInt32(b, at + 24);
            if (strOff + strLen > b.Length) return null;
            list.Add(new Entry(
                System.Text.Encoding.UTF8.GetString(b, (int)strOff, (int)strLen),
                BitConverter.ToUInt32(b, at),
                BitConverter.ToUInt32(b, at + 8)));
        }
        return list;
    }

    // FpkString header: offset u32 @0, length u32 @8. `at` is relative to `tables`.
    private static long StringEnd(byte[] tables, int at)
    {
        if (at + 12 > tables.Length) return 0;
        long off = BitConverter.ToUInt32(tables, at);
        long len = BitConverter.ToUInt32(tables, at + 8);
        return off + len;
    }
}
