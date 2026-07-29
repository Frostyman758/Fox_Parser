// Browse tree nodes: dirs, files, lazy regions
using MgsvModBldr.Tools.Qar;
using MgsvModBldr.Tools.G0s;

namespace MgsvModBldr.Tools.Browse;

// The synthesised directory tree an ArchiveHandle exposes. An archive stores a
// flat list of entries with full interior paths; we fold those into folders so
// callers can ask "what's in this directory?" instead of parsing paths.

public sealed class DirNode
{
    public SortedDictionary<string, DirNode> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FileNode> Files { get; } = new();
}

public sealed class FileNode
{
    public string Name = "";
    public ulong  Size;
    public ulong  Hash;
    public bool   IsArchive;     // a nested container we can drill into

    // Set per owning-archive kind for the formats still read eagerly (QAR + G0s
    // decrypt per entry). Everything else — fpk/pftxs (TPP and GZ)/sbp/stp/sab/
    // fsop/mtar — uses Lazy instead.
    public QarEntry?     Qar;
    public G0sEntry?     G0s;

    // Resolved-in-memory bytes for entries that are plain blobs (mtar .enchnk).
    // A blob that is itself an archive is re-detected by magic when drilled into.
    public byte[]?       Blob;

    // LAZY entry: read on demand from the archive's source (file or nested
    // bytes) instead of being materialised at open time. Big archives then cost
    // only their index, and a file is decoded only when actually touched.
    public LazyBlob?     Lazy;
}

// A region of the owning archive that holds one file's bytes, decoded on read.
public sealed class LazyBlob
{
    public long   Offset;
    public int    Length;
    public byte   Decode;     // 0 = raw, 1 = fpk crypto (Key = entry path), 2 = xor 0x9C
    public string Key = "";

    public const byte Raw = 0, Fpk = 1, Xor9C = 2;
}
