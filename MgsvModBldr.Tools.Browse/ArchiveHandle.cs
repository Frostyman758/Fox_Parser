// Browsable model of one opened Fox archive
using MgsvModBldr.Tools.Qar;
using MgsvModBldr.Tools.Fpk;
using MgsvModBldr.Tools.Fpk.Gz;
using MgsvModBldr.Tools.Pftxs.Gz;
using MgsvModBldr.Tools.G0s;

namespace MgsvModBldr.Tools.Browse;

// Opening is CONTENT-driven: sniff the first bytes with FoxFormats.Detect and
// dispatch to the matching reader. No extension check — any path whose bytes
// are a real Fox magic browses; anything else throws InvalidDataException.
// Tree construction lives in ArchiveHandle.Tree.cs.
public sealed partial class ArchiveHandle : IDisposable
{
    internal enum Kind { Qar, Fpk, GzFpk, Pftxs, GzPftxs, G0s, Sbp, Stp, Sab, Fsop, Mtar }

    private readonly Kind _kind;
    private readonly string? _filePath;   // top-level: reopen per read
    private readonly byte[]? _rawBytes;    // nested: backing bytes (no copy on reopen)
    private readonly QarFile? _qar;
    private G0sArchive? _g0s;              // G0s index (footer + entry table)
    private readonly object _readLock = new();

    public DirNode Root { get; } = new();

    private ArchiveHandle(Kind kind, string? filePath, byte[]? rawBytes, QarFile? qar)
    {
        _kind = kind; _filePath = filePath; _rawBytes = rawBytes; _qar = qar;
    }

    private Stream OpenSource()
        => _filePath is not null ? File.OpenRead(_filePath)
                                 : new MemoryStream(_rawBytes!, writable: false);

    // ── Opening ────────────────────────────────────────────────────────────

    public static ArchiveHandle OpenPath(string path)
    {
        Span<byte> magic = stackalloc byte[FoxFormats.SniffBytes];
        FoxFormat fmt;
        using (var fs = File.OpenRead(path))
        {
            int n = ReadAtMost(fs, magic);
            fmt = FoxFormats.Detect(magic[..n]);
            if (fmt == FoxFormat.Unknown)        // .g0s has no header — check the footer
            {
                Span<byte> tail = stackalloc byte[FoxFormats.FooterBytes];
                if (ReadTail(fs, tail) == tail.Length && FoxFormats.IsG0sFooter(tail))
                    fmt = FoxFormat.G0s;
            }
        }

        // Headerless containers, identified by extension when nothing matched.
        if (fmt == FoxFormat.Unknown && path.EndsWith(".fsop", StringComparison.OrdinalIgnoreCase))
        {
            // Detect from the bytes, but open lazily against the file (the detect
            // buffer is dropped; reads come from disk on demand).
            var bytes = File.ReadAllBytes(path);
            if (FoxFormats.IsFsop(bytes)) return OpenFsop(path, null);   // confirmed by structure
        }
        if (fmt == FoxFormat.Unknown && path.EndsWith(".mtar", StringComparison.OrdinalIgnoreCase))
            return OpenMtar(path, null);                            // .mtar has no detectable magic

        return fmt switch
        {
            FoxFormat.Qar => OpenQar(path, null),
            FoxFormat.Fpk or FoxFormat.Fpkd => OpenFpk(path, null),
            FoxFormat.Pftxs => OpenPftxs(path, null),
            FoxFormat.G0s => OpenG0s(path, null),
            FoxFormat.Sbp => OpenSbp(path, null),
            FoxFormat.Stp => OpenStp(path, null),
            FoxFormat.Sab => OpenSab(path, null),
            _ => throw new InvalidDataException("not a recognised Fox archive"),
        };
    }

    // `name` is the entry's leaf name (e.g. "foo.mtar"); used only to identify
    // headerless containers (.mtar) that have no detectable magic.
    public static ArchiveHandle OpenNestedBytes(byte[] bytes, string? name = null)
    {
        var magic = bytes.AsSpan(0, Math.Min(bytes.Length, FoxFormats.SniffBytes));
        var fmt = FoxFormats.Detect(magic);
        if (fmt == FoxFormat.Unknown
            && bytes.Length >= FoxFormats.FooterBytes
            && FoxFormats.IsG0sFooter(bytes.AsSpan(bytes.Length - FoxFormats.FooterBytes)))
            fmt = FoxFormat.G0s;

        // Headerless containers. .fsop is confirmed by its exact structure; .mtar
        // has no detectable magic, so it's trusted by extension (OpenNested is
        // only reached for entries the listing already flagged as containers).
        if (fmt == FoxFormat.Unknown && FoxFormats.IsFsop(bytes))
            return OpenFsop(null, bytes);
        if (fmt == FoxFormat.Unknown && name is not null
            && name.EndsWith(".mtar", StringComparison.OrdinalIgnoreCase))
            return OpenMtar(null, bytes);

        return fmt switch
        {
            FoxFormat.Qar => OpenQar(null, bytes),
            FoxFormat.Fpk or FoxFormat.Fpkd => OpenFpk(null, bytes),
            FoxFormat.Pftxs => OpenPftxs(null, bytes),
            FoxFormat.G0s => OpenG0s(null, bytes),
            FoxFormat.Sbp => OpenSbp(null, bytes),
            FoxFormat.Stp => OpenStp(null, bytes),
            FoxFormat.Sab => OpenSab(null, bytes),
            _ => throw new InvalidDataException("nested blob is not a recognised Fox archive"),
        };
    }

    // path != null => top-level (reopened per read); bytes != null => nested.

    private static ArchiveHandle OpenQar(string? path, byte[]? bytes)
    {
        var qar = new QarFile();
        if (path is not null)
        {
            qar.ReadFrom(path);          // headers only — cheap, no entry bodies
            qar.Close();                 // release its FileStream; we reopen per read
        }
        else
        {
            using var ms = new MemoryStream(bytes!, writable: false);
            qar.Read(ms);
            qar.Close();
        }
        var h = new ArchiveHandle(Kind.Qar, path, bytes, qar);
        h.BuildQarTree();
        return h;
    }

    private static ArchiveHandle OpenFpk(string? path, byte[]? bytes)
    {
        // Route GZ "ste" fpk to the dedicated GZ reader — the TPP FpkFile below
        // is left completely untouched. (GZ fpk only appears nested in a .g0s.)
        Span<byte> head = stackalloc byte[10];
        PeekHead(path, bytes, head);
        if (GzFpkFile.IsGzMagic(head)) return OpenGzFpk(path, bytes);

        // LAZY: read only the fpk index; entry bytes are pulled on demand.
        var h = new ArchiveHandle(Kind.Fpk, path, bytes, null);
        using (var s = h.OpenSource()) h.BuildFpkTree(LazyFpkReader.Read(s));
        return h;
    }

    private static ArchiveHandle OpenGzFpk(string? path, byte[]? bytes)
    {
        GzFpkFile fpk;
        if (path is not null) { using var fs = File.OpenRead(path); fpk = GzFpkFile.Read(fs); }
        else { using var ms = new MemoryStream(bytes!, writable: false); fpk = GzFpkFile.Read(ms); }
        var h = new ArchiveHandle(Kind.GzFpk, path, bytes, null);
        h.BuildGzFpkTree(fpk);
        return h;
    }

    private static ArchiveHandle OpenPftxs(string? path, byte[]? bytes)
    {
        // GZ pftxs (float 1.0 at offset 4) uses a different layout — route it to
        // the dedicated GZ reader; the TPP lazy reader below is left untouched.
        Span<byte> head = stackalloc byte[8];
        PeekHead(path, bytes, head);
        if (GzPftxsFile.IsGzPftxs(head)) return OpenGzPftxs(path, bytes);

        // LAZY: read only the PFTX/TEXL/FTEX index; texture bytes pulled on demand.
        var h = new ArchiveHandle(Kind.Pftxs, path, bytes, null);
        using (var s = h.OpenSource()) h.BuildPftxsTree(LazyPftxsReader.Read(s));
        return h;
    }

    private static ArchiveHandle OpenGzPftxs(string? path, byte[]? bytes)
    {
        GzPftxsFile pftxs;
        if (path is not null) { using var fs = File.OpenRead(path); pftxs = GzPftxsFile.Read(fs); }
        else { using var ms = new MemoryStream(bytes!, writable: false); pftxs = GzPftxsFile.Read(ms); }
        var h = new ArchiveHandle(Kind.GzPftxs, path, bytes, null);
        h.BuildGzPftxsTree(pftxs);
        return h;
    }

    private static ArchiveHandle OpenG0s(string? path, byte[]? bytes)
    {
        G0sArchive arc;
        if (path is not null) { using var fs = File.OpenRead(path); arc = G0sArchive.ReadIndex(fs); }
        else { using var ms = new MemoryStream(bytes!, writable: false); arc = G0sArchive.ReadIndex(ms); }
        var h = new ArchiveHandle(Kind.G0s, path, bytes, null) { _g0s = arc };
        h.BuildG0sTree();
        return h;
    }

    private static ArchiveHandle OpenSbp(string? path, byte[]? bytes)
    {
        // LAZY: read only the SBP entry table; sub-file bytes pulled on demand.
        var h = new ArchiveHandle(Kind.Sbp, path, bytes, null);
        using (var s = h.OpenSource()) h.BuildSbpTree(LazySbpReader.Read(s));
        return h;
    }

    private static ArchiveHandle OpenStp(string? path, byte[]? bytes)
    {
        // LAZY: read only the STP entry table; wem/ls2 bytes pulled on demand.
        var h = new ArchiveHandle(Kind.Stp, path, bytes, null);
        using (var s = h.OpenSource()) h.BuildStpTree(LazyStpReader.Read(s));
        return h;
    }

    private static ArchiveHandle OpenSab(string? path, byte[]? bytes)
    {
        // LAZY: read only the SAB entry table; lsst bytes pulled on demand.
        var h = new ArchiveHandle(Kind.Sab, path, bytes, null);
        using (var s = h.OpenSource()) h.BuildSabTree(LazySabReader.Read(s));
        return h;
    }

    private static ArchiveHandle OpenFsop(string? path, byte[]? bytes)
    {
        // LAZY: walk the structure for offsets/sizes only; vs/ps DXBC blobs are
        // XOR-0x9C decoded on demand. (Detection already confirmed the structure.)
        var h = new ArchiveHandle(Kind.Fsop, path, bytes, null);
        using (var s = h.OpenSource()) h.BuildFsopTree(LazyFsopReader.Read(s));
        return h;
    }

    private static ArchiveHandle OpenMtar(string? path, byte[]? bytes)
    {
        // LAZY: parse the v1/v2 index (offsets/sizes only) by driving the verified
        // MtarFile/MtarFile2 readers; each gani/trk/chnk/exchnk is pulled on demand.
        // (.enchnk size needs a scan, so its tiny block is read once at open.)
        var h = new ArchiveHandle(Kind.Mtar, path, bytes, null);
        using (var s = h.OpenSource()) h.BuildMtarTree(LazyMtarReader.Read(s));
        return h;
    }

    // ── Listing / navigation ─────────────────────────────────────────────────

    public DirNode? NavigateDir(string dirPath)
    {
        var dir = Root;
        if (string.IsNullOrEmpty(dirPath)) return dir;
        foreach (var part in dirPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!dir.Dirs.TryGetValue(part, out var child)) return null;
            dir = child;
        }
        return dir;
    }

    public FileNode? FindFile(string interiorPath)
    {
        var norm = interiorPath.Replace('\\', '/').TrimStart('/');
        int slash = norm.LastIndexOf('/');
        var dirPart = slash < 0 ? "" : norm[..slash];
        var leaf = slash < 0 ? norm : norm[(slash + 1)..];
        var dir = NavigateDir(dirPart);
        if (dir is null) return null;
        foreach (var f in dir.Files)
            if (string.Equals(f.Name, leaf, StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    public byte[] ReadFile(FileNode node)
    {
        if (node.Lazy is { } lz)          // pull this entry's region on demand
        {
            var buf = new byte[lz.Length];
            lock (_readLock)
            {
                using var s = OpenSource();
                s.Seek(lz.Offset, SeekOrigin.Begin);
                ReadExactInto(s, buf);
            }
            return Decode(buf, lz);
        }

        if (node.Blob is { } blob)       // resolved-in-memory (mtar .enchnk; rare)
            return blob;

        // GZ fpk/pftxs are nested-only (always opened from an already-in-memory
        // g0s blob), so their bytes are decoded up front — lazy would save nothing.
        if (_kind == Kind.GzFpk)
            return node.GzFpk!.Data;
        if (_kind == Kind.GzPftxs)
            return node.GzPftxs!.Data;

        if (_kind == Kind.G0s)           // GZ QAR: read raw blob, then decrypt
        {
            var e = node.G0s!;
            var raw = new byte[e.Size];
            lock (_readLock)
            {
                using var s = OpenSource();
                s.Seek(16L * e.Offset, SeekOrigin.Begin);
                ReadExactInto(s, raw);
            }
            // Decrypt mutates `raw` (outer pass) then unwraps the inner cipher if
            // present; we discard the inner key (read-only browsing).
            var (data, _) = G0sArchive.Decrypt(raw, e.Offset);
            return data;
        }

        lock (_readLock)                 // QAR: reopen + lazy-decrypt this entry
        {
            using var s = OpenSource();
            node.Qar!.ReadData(s);
            var data = node.Qar.Data;
            // Release the bytes immediately — do NOT retain file data on the
            // cached index. The cache holds only the table of contents (entry
            // names/offsets/sizes), never the (potentially GB) file contents.
            node.Qar.Data = System.Array.Empty<byte>();
            node.Qar.Loaded = false;
            return data;
        }
    }

    private static int ReadAtMost(Stream s, Span<byte> buf)
    {
        int n = 0;
        while (n < buf.Length)
        {
            int r = s.Read(buf[n..]);
            if (r == 0) break;
            n += r;
        }
        return n;
    }

    // Copy the first buf.Length bytes from a nested blob or a file on disk (used
    // to tell GZ "ste" fpk apart from TPP "win" fpk before choosing a reader).
    private static void PeekHead(string? path, byte[]? bytes, Span<byte> buf)
    {
        buf.Clear();
        if (bytes is not null)
        {
            bytes.AsSpan(0, Math.Min(bytes.Length, buf.Length)).CopyTo(buf);
            return;
        }
        using var fs = File.OpenRead(path!);
        ReadAtMost(fs, buf);
    }

    // Read the last buf.Length bytes of a seekable stream (for footer sniffing).
    private static int ReadTail(Stream s, Span<byte> buf)
    {
        if (!s.CanSeek || s.Length < buf.Length) return 0;
        s.Seek(-buf.Length, SeekOrigin.End);
        return ReadAtMost(s, buf);
    }

    private static void ReadExactInto(Stream s, Span<byte> buf)
    {
        if (ReadAtMost(s, buf) != buf.Length) throw new EndOfStreamException();
    }

    // Decode a raw lazy region into the file's plaintext bytes.
    private static byte[] Decode(byte[] b, LazyBlob lz)
    {
        if (lz.Decode == LazyBlob.Fpk)
        {
            // FPK entries are encrypted only when they begin 0x1B/0x1C; the key
            // is the entry path. Matches FpkEntry.ReadData exactly.
            if (b.Length > 0 && (b[0] == 0x1B || b[0] == 0x1C)
                && FpkCrypto.TryDecrypt(b, lz.Key, out var dec)) return dec;
            return b;
        }
        if (lz.Decode == LazyBlob.Xor9C)
        {
            for (int i = 0; i < b.Length; i++) b[i] ^= 0x9C;
            return b;
        }
        return b;
    }

    public void Dispose() => _qar?.Close();
}
