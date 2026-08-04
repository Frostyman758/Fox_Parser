// GZ fpk and pftxs indexes
namespace MgsvModBldr.Tools.Index;

/// <summary>
/// GZ .fpk/.fpkd ("ste" platform tag). Same 48-byte header and 48-byte entry
/// records as TPP, with one simplification: entry paths are MD5-resolved rather
/// than stored as strings, so there is no string region to chase — the index ends
/// at the tables. Names are left to the caller to resolve from the MD5.
/// </summary>
public static class GzFpkIndex
{
    private const int Header = 48, EntrySize = 48, StrHeader = 16;

    /// <param name="RawText">The entry's own string bytes. Sometimes the real path;
    /// otherwise it still carries the EXTENSION, which is what makes an unresolved
    /// entry identifiable as an .mtar/.gani rather than an opaque hash.</param>
    public sealed record Entry(byte[] PathMd5, string RawText, uint DataOffset, uint DataSize);

    public static List<Entry> Read(RangeReader read, long totalSize, out int bytesRead)
    {
        bytesRead = 0;
        var head = read(0, Header);
        if (head is null || head.Length < Header) return null;
        var kind = ContainerKind.Detect(head);
        if (kind is not (Container.GzFpk or Container.GzFpkd)) return null;

        uint entryCount = BitConverter.ToUInt32(head, 36);
        uint refCount = BitConverter.ToUInt32(head, 40);
        if (entryCount > 1_000_000 || refCount > 1_000_000) return null;

        long tablesEnd = Header + (long)entryCount * EntrySize + (long)refCount * StrHeader;
        if (tablesEnd > totalSize) return null;

        var tables = read(Header, (int)(tablesEnd - Header));
        if (tables is null || tables.Length < tablesEnd - Header) return null;
        bytesRead = head.Length + tables.Length;

        // Each entry's FpkString header (at +16) points at string bytes living AFTER
        // the tables. Read out to the furthest one — that text is the only way an
        // unresolved entry keeps its extension.
        long strEnd = tablesEnd;
        for (int i = 0; i < entryCount; i++)
        {
            int sh = i * EntrySize + 16;
            long off = BitConverter.ToUInt32(tables, sh);
            long len = BitConverter.ToUInt32(tables, sh + 8);
            if (off >= tablesEnd) strEnd = Math.Max(strEnd, off + len);
        }
        byte[] blob = null;
        if (strEnd > tablesEnd && strEnd <= totalSize)
            blob = read(0, (int)strEnd);
        bytesRead = blob?.Length ?? bytesRead;

        var list = new List<Entry>((int)entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            int at = i * EntrySize;
            var md5 = new byte[16];
            Array.Copy(tables, at + 32, md5, 0, 16);

            string raw = "";
            uint sOff = BitConverter.ToUInt32(tables, at + 16);
            uint sLen = BitConverter.ToUInt32(tables, at + 24);
            if (blob is not null && sOff + sLen <= blob.Length && sLen > 0)
                raw = System.Text.Encoding.Latin1.GetString(blob, (int)sOff, (int)sLen);

            list.Add(new Entry(md5, raw,
                BitConverter.ToUInt32(tables, at),
                BitConverter.ToUInt32(tables, at + 8)));
        }
        return list;
    }
}

/// <summary>
/// GZ .pftxs. Header (20B): magic | float 1.0 | size | fileCount | dataOffset,
/// then fileCount x { nameOffset u32, ftexSize u32 }, then a null-terminated name
/// region — all of it before dataOffset. The header states where the data starts,
/// so one read of dataOffset bytes is the whole index, no sizing pass.
/// </summary>
public static class GzPftxsIndex
{
    private const int Header = 20, EntrySize = 8;

    public sealed record Entry(string Name, int Size);

    public static List<Entry> Read(RangeReader read, long totalSize, out int bytesRead)
    {
        bytesRead = 0;
        var head = read(0, ContainerKind.SniffBytes < Header ? Header : ContainerKind.SniffBytes);
        if (head is null || head.Length < Header) return null;
        if (ContainerKind.Detect(head) != Container.GzPftxs) return null;

        int fileCount = BitConverter.ToInt32(head, 12);
        int dataOffset = BitConverter.ToInt32(head, 16);
        if (fileCount < 0 || fileCount > 1_000_000) return null;
        if (dataOffset < Header + fileCount * EntrySize || dataOffset > totalSize) return null;

        var b = read(0, dataOffset);
        if (b is null || b.Length < dataOffset) return null;
        bytesRead = b.Length;

        var list = new List<Entry>(fileCount);
        for (int i = 0; i < fileCount; i++)
        {
            int at = Header + i * EntrySize;
            int nameOffset = BitConverter.ToInt32(b, at);
            int size = BitConverter.ToInt32(b, at + 4);
            list.Add(new Entry(NullTermAt(b, nameOffset), size));
        }
        return list;
    }

    private static string NullTermAt(byte[] b, int at)
    {
        if (at < 0 || at >= b.Length) return "";
        int end = at;
        while (end < b.Length && b[end] != 0) end++;
        return System.Text.Encoding.UTF8.GetString(b, at, end - at);
    }
}
