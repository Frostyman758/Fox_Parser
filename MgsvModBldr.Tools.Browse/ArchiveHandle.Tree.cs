// Tree construction per archive format
using MgsvModBldr.Tools.Qar;
using MgsvModBldr.Tools.G0s;

namespace MgsvModBldr.Tools.Browse;

// Turn each archive's flat entry list into the DirNode/FileNode hierarchy the
// caller walks. One BuildXxxTree per format.
public sealed partial class ArchiveHandle
{
    private void BuildQarTree()
    {
        var dict = QarNameDictionary.Get();
        foreach (var e in _qar!.Entries)
        {
            string path;
            if (dict is not null)
            {
                path = dict.Resolve(e.Header.PathHash, out bool found);
                if (!found) path = $"_unresolved/{e.Header.PathHash:x16}";
            }
            else path = $"_unresolved/{e.Header.PathHash:x16}";

            e.Header.FilePath = path;
            // UncompressedSize includes the encryption data-header for encrypted
            // entries; subtract it so the listed size matches extracted bytes.
            ulong dh = (ulong)QarConstants.GetDataHeaderSize(e.DataHeader.EncryptionMagic);
            ulong shownSize = e.Header.UncompressedSize > dh
                ? e.Header.UncompressedSize - dh
                : e.Header.UncompressedSize;
            var leaf = AddPath(path, shownSize, e.Header.PathHash);
            leaf.Qar = e;
            leaf.IsArchive = FoxFormats.IsNestedContainer(leaf.Name);
        }
    }

    private void BuildFpkTree(List<LazyFpkReader.Entry> entries)
    {
        foreach (var e in entries)         // index only — bytes pulled on demand
        {
            var leaf = AddPath(e.Path, (ulong)e.DataSize, 0);
            leaf.Lazy = new LazyBlob { Offset = e.DataOffset, Length = e.DataSize, Decode = LazyBlob.Fpk, Key = e.Path };
            leaf.IsArchive = FoxFormats.IsNestedContainer(leaf.Name);
        }
    }

    private void BuildGzFpkTree(List<LazyGzFpkReader.Entry> entries)
    {
        // Entry paths are MD5-resolved (real path from fpk_dictionary, else
        // <md5hex><ext>); see GzFpkString. Data is plaintext, pulled on demand.
        foreach (var e in entries)
        {
            var leaf = AddPath(e.Path, (ulong)e.DataSize, 0);
            leaf.Lazy = new LazyBlob { Offset = e.DataOffset, Length = e.DataSize, Decode = LazyBlob.Raw };
            leaf.IsArchive = FoxFormats.IsGzNestedContainer(leaf.Name);
        }
    }

    private void BuildGzPftxsTree(List<LazyGzPftxsReader.Entry> entries)
    {
        foreach (var e in entries)       // flat .ftex + .ftexs entries, pulled on demand
        {
            var leaf = AddPath(e.Path, (ulong)e.Size, 0);
            leaf.Lazy = new LazyBlob { Offset = e.Offset, Length = e.Size, Decode = LazyBlob.Raw };
        }
    }

    // .sbp — sub-files tagged by a 4-byte magic (bnk/stp/sab), no names. Name
    // them "<index>.<tag>"; the stp/sab ones are themselves containers (drilled
    // into by magic) while bnk is a leaf.
    private void BuildSbpTree(List<LazySbpReader.Entry> entries)
    {
        for (int i = 0; i < entries.Count; i++)   // index only — sub-files pulled on demand
        {
            var e = entries[i];
            var tag = string.IsNullOrEmpty(e.Magic) ? "bin" : e.Magic;
            var leaf = AddPath($"{i}.{tag}", (ulong)e.DataSize, 0);
            leaf.Lazy = new LazyBlob { Offset = e.DataOffset, Length = e.DataSize, Decode = LazyBlob.Raw };
            leaf.IsArchive = FoxFormats.IsNestedContainer(leaf.Name);
        }
    }

    // .stp — each entry is a hash + a .wem (and, on TPP, a .ls2 lipsync).
    private void BuildStpTree(List<LazyStpReader.Entry> entries)
    {
        foreach (var e in entries)         // index only — wem/ls2 pulled on demand
        {
            var stem = e.Name.ToString("x8");
            var wem = AddPath($"{stem}.wem", (ulong)e.WemSize, e.Name);
            wem.Lazy = new LazyBlob { Offset = e.WemOffset, Length = e.WemSize, Decode = LazyBlob.Raw };
            if (e.Ls2Size >= 0)
            {
                var ls2 = AddPath($"{stem}.ls2", (ulong)e.Ls2Size, e.Name);
                ls2.Lazy = new LazyBlob { Offset = e.Ls2Offset, Length = e.Ls2Size, Decode = LazyBlob.Raw };
            }
        }
    }

    // .sab — each entry is a 64-bit hash + a combined .lsst lip-sync block.
    private void BuildSabTree(List<LazySabReader.Entry> entries)
    {
        foreach (var e in entries)         // index only — lsst pulled on demand
        {
            var leaf = AddPath($"{e.Name:x16}.lsst", (ulong)e.Size, e.Name);
            leaf.Lazy = new LazyBlob { Offset = e.Offset, Length = e.Size, Decode = LazyBlob.Raw };
        }
    }

    // .fsop — each shader becomes "<name>_vs.fxc" + "<name>_ps.fxc" (DXBC blobs,
    // XOR-0x9C decoded on demand). The shader's own name is used VERBATIM: the
    // leading index was never part of the file, and AddPath just List-appends,
    // so a duplicate name lists both rather than clobbering.
    private void BuildFsopTree(List<LazyFsopReader.Entry> shaders)
    {
        foreach (var s in shaders)   // index only — blobs decoded on demand
        {
            var vs = AddPath($"{s.Name}_vs.fxc", (ulong)s.VsSize, 0);
            vs.Lazy = new LazyBlob { Offset = s.VsOffset, Length = s.VsSize, Decode = LazyBlob.Xor9C };
            var ps = AddPath($"{s.Name}_ps.fxc", (ulong)s.PsSize, 0);
            ps.Lazy = new LazyBlob { Offset = s.PsOffset, Length = s.PsSize, Decode = LazyBlob.Xor9C };
        }
    }

    // .mtar — the flat gani/trk/chnk/exchnk/enchnk file set. Lazy for everything
    // except the rare .enchnk (whose size needs a scan, so it's read at open).
    private void BuildMtarTree(List<LazyMtarReader.Item> items)
    {
        foreach (var e in items)
        {
            if (e.Eager is not null)       // .enchnk — already in memory (tiny)
            {
                var leaf = AddPath(e.Name, (ulong)e.Eager.LongLength, 0);
                leaf.Blob = e.Eager;
            }
            else
            {
                var leaf = AddPath(e.Name, (ulong)e.Size, 0);
                leaf.Lazy = new LazyBlob { Offset = e.Offset, Length = e.Size, Decode = LazyBlob.Raw };
            }
        }
    }

    private void BuildPftxsTree(List<LazyPftxsReader.Entry> entries)
    {
        var dict = QarNameDictionary.Get();
        foreach (var e in entries)         // index only — texture bytes pulled on demand
        {
            string path;
            if (dict is not null)
            {
                path = dict.Resolve(e.Hash, out bool found);
                if (!found) path = $"_unresolved/{e.Hash:x16}.ftex";
            }
            else path = $"_unresolved/{e.Hash:x16}.ftex";

            var leaf = AddPath(path, (ulong)e.DataSize, e.Hash);
            leaf.Lazy = new LazyBlob { Offset = e.DataOffset, Length = e.DataSize, Decode = LazyBlob.Raw };
            leaf.IsArchive = FoxFormats.IsNestedContainer(leaf.Name);
        }
    }

    private void BuildG0sTree()
    {
        // G0sHash.TryResolve always yields a path: the dictionary stem (if found)
        // or the 48-bit hash in hex, plus the GZ typeId's extension. So even
        // without gzs_dictionary.txt, entries browse with correct extensions.
        foreach (var e in _g0s!.Entries)
        {
            G0sHash.TryResolve(e.Hash, out var path);
            if (string.IsNullOrEmpty(path)) path = $"_unresolved/{e.Hash:x16}";
            // Size is the raw on-disk blob (includes the 8-byte inner header for
            // inner-encrypted entries); good enough for the listing.
            var leaf = AddPath(path, e.Size, e.Hash);
            leaf.G0s = e;
            leaf.IsArchive = FoxFormats.IsGzNestedContainer(leaf.Name);
        }
    }

    // Insert a file at its interior path, creating intermediate folders.
    private FileNode AddPath(string rawPath, ulong size, ulong hash)
    {
        var norm = rawPath.Replace('\\', '/').TrimStart('/');
        var parts = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var dir = Root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!dir.Dirs.TryGetValue(parts[i], out var child))
            {
                child = new DirNode();
                dir.Dirs[parts[i]] = child;
            }
            dir = child;
        }
        var leafName = parts.Length > 0 ? parts[^1] : norm;
        var node = new FileNode { Name = leafName, Size = size, Hash = hash };
        dir.Files.Add(node);
        return node;
    }
}
