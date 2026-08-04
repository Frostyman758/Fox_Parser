// Game-path (virtual) listing of one archive
using MgsvModBldr.Tools.G0s;
using MgsvModBldr.Tools.Index;
using MgsvModBldr.Tools.Qar;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// Lists ONE archive by the paths the GAME uses rather than by container nesting:
/// a model inside plparts_sna2_main0_def_v00.fpk appears at
/// Assets/tpp/chara/sna/Scenes/…, not under the pack.
///
/// Costs each container its INDEX, not its payload — a chunk archive with ~2,800
/// packs lists for a couple of MB instead of decoding every pack.
///
/// Only .fpk/.fpkd and .pftxs are flattened in. Their contents have game paths (an
/// fpk stores real path strings; a pftxs piece resolves through the dictionary).
/// An .sbp's slots are positional ("0.bnk") and an .fsop's shaders are bare names,
/// so hoisting those would strand unrooted names at the archive root — they stay
/// containers you enter instead.
///
/// Per-archive only. Nothing here knows which archive outranks which; that's
/// install knowledge and belongs to the mod tools.
/// </summary>
public static class VirtualListing
{
    /// <param name="VirtualPath">Where the game resolves this file.</param>
    /// <param name="Pack">Containing container's path, null when the entry is loose.</param>
    /// <param name="Interior">Path within that container, null when loose.</param>
    public sealed record Item(string VirtualPath, string Pack, string Interior, long Size, ulong Hash)
    {
        public bool InPack => Pack is not null;
        /// <summary>The route `stream` takes to the bytes.</summary>
        public string PhysicalPath => Pack is null ? VirtualPath : $"{Pack}/{Interior}";
    }

    public sealed record Result(List<Item> Items, int ContainersIndexed, long IndexBytes);

    public static Result Build(string archivePath)
    {
        var kind = ArchiveFormat.Detect(archivePath);
        if (!ArchiveFormat.IsArchive(kind)) throw new InvalidDataException(ArchiveFormat.Describe(archivePath, kind));
        return kind == FoxArchiveKind.G0s ? BuildG0s(archivePath) : BuildQar(archivePath);
    }

    private static Result BuildQar(string archivePath)
    {
        var qr = new QarReader(archivePath);
        var dict = QarDictionary.Load();
        using var fs = File.OpenRead(archivePath);

        var items = new List<Item>(qr.Entries.Count * 2);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int containers = 0;
        long indexBytes = 0;

        foreach (var e in qr.Entries)
        {
            string name = e.Header.FilePath;
            if (string.IsNullOrEmpty(name))
            {
                var r = dict.Resolve(e.Header.PathHash, out bool found);
                if (found) name = r;
            }
            string vpath = Norm(string.IsNullOrEmpty(name) ? $"_unresolved/{e.Header.PathHash:x16}" : name);
            if (seen.Add(vpath))
                items.Add(new Item(vpath, null, null, e.Header.UncompressedSize, e.Header.PathHash));

            // Containers are found by MAGIC: a hash-only entry has no ".fpk" to
            // match on, and skipping it would hide everything inside it.
            var src = RangeSources.ForQar(e, fs);
            long plain = RangeSources.PlainSize(e);
            if (plain < ContainerKind.SniffBytes) continue;
            var ck = ContainerKind.Detect(src);

            if (ContainerKind.IsPack(ck))
            {
                var idx = FpkIndex.Read(src, plain, out int used);
                if (idx is null) continue;
                containers++; indexBytes += used;
                foreach (var inner in idx)
                {
                    var ip = Norm(inner.Path);
                    if (ip.Length == 0 || !seen.Add(ip)) continue;   // a loose copy outranks a packed one
                    items.Add(new Item(ip, vpath, ip, inner.DataSize, 0));
                }
            }
            else if (ck == Container.Pftxs)
            {
                var tex = PftxsIndex.Read(src, plain, out int used);
                if (tex is null) continue;
                containers++; indexBytes += used;
                foreach (var piece in tex)
                {
                    var pr = dict.Resolve(piece.Hash, out bool pfound);
                    var ip = Norm(pfound ? pr : $"_unresolved/{piece.Hash:x16}.ftex");
                    if (!seen.Add(ip)) continue;
                    items.Add(new Item(ip, vpath, ip, piece.Size, piece.Hash));
                }
            }
        }
        return new Result(items, containers, indexBytes);
    }

    private static Result BuildG0s(string archivePath)
    {
        var gr = new GzReader(archivePath);
        using var fs = File.OpenRead(archivePath);

        var items = new List<Item>(gr.Entries.Count * 2);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int containers = 0;
        long indexBytes = 0;

        foreach (var e in gr.Entries)
        {
            string vpath = Norm(G0sHash.TryResolve(e.Hash, out var p) ? p : $"_unresolved/{e.Hash:x16}");
            if (seen.Add(vpath))
                items.Add(new Item(vpath, null, null, e.Size, e.Hash));

            if (e.Size < ContainerKind.SniffBytes) continue;
            var src = RangeSources.ForG0s(e, fs);
            var ck = ContainerKind.Detect(src);
            long plain = RangeSources.PlainSize(e, fs);

            if (ck is Container.GzFpk or Container.GzFpkd)
            {
                // A GZ fpk stores entry paths as MD5, not as strings (TPP stores
                // strings) — so names come from fpk_dictionary.txt. Without that the
                // listing is hex and no text search can ever match a filename.
                var idx = GzFpkIndex.Read(src, plain, out int used);
                if (idx is null) continue;
                containers++; indexBytes += used;
                foreach (var inner in idx)
                {
                    var ip = Norm(GzName(inner, vpath));
                    if (!seen.Add(ip)) continue;
                    items.Add(new Item(ip, vpath, ip, inner.DataSize, 0));
                }
            }
            else if (ck == Container.GzPftxs)
            {
                var tex = GzPftxsIndex.Read(src, plain, out int used);
                if (tex is null) continue;
                containers++; indexBytes += used;
                foreach (var t in tex)
                {
                    var ip = Norm(t.Name);
                    if (ip.Length == 0 || !seen.Add(ip)) continue;
                    items.Add(new Item(ip, vpath, ip, t.Size, 0));
                }
            }
        }
        return new Result(items, containers, indexBytes);
    }

    // Mirrors GzFpkString.Resolve: the entry's own string wins when its MD5 proves
    // it IS the path, then fpk_dictionary, then "<md5hex><ext>". Keeping the
    // extension is what makes an unnamed entry still identifiable as an .mtar or
    // .gani instead of an opaque hash no search can ever match.
    private static string GzName(GzFpkIndex.Entry e, string packPath)
    {
        var raw = e.RawText ?? "";
        if (e.PathMd5 is { Length: 16 } && !AllZero(e.PathMd5))
        {
            if (raw.Length > 0)
            {
                var bytes = System.Text.Encoding.Latin1.GetBytes(raw);
                if (System.Security.Cryptography.MD5.HashData(bytes).AsSpan().SequenceEqual(e.PathMd5))
                    return raw;                                   // raw string IS the path
            }
            if (MgsvModBldr.Tools.Fpk.Gz.FpkDictionary.TryResolve(e.PathMd5, out var real))
                return real;

            int dot = raw.LastIndexOf('.');
            var ext = dot >= 0 ? raw[dot..] : "";
            return $"{packPath}/_unresolved/{Convert.ToHexString(e.PathMd5).ToLowerInvariant()}{ext}";
        }
        return raw.Length > 0 ? raw : $"{packPath}/_unresolved/(noname)";
    }

    private static bool AllZero(byte[] b)
    {
        foreach (var x in b) if (x != 0) return false;
        return true;
    }

    private static string Norm(string s) => s is null ? "" : s.Replace('\\', '/').TrimStart('/');
}
