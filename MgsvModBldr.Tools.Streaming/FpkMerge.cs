// Merge vanilla + modded fpk contents
using MgsvModBldr.Tools.Fpk;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// Merges a vanilla .fpk/.fpkd with one or more mod versions: union of inner
/// files, later mods win per inner-path, vanilla fills the rest. This is what
/// makes overlapping mods coexist (the thing whole-file override broke).
/// Re-encoded with fox_parser's FpkFile, which round-trips byte-exact.
/// </summary>
public static class FpkMerge
{
    public static byte[] Merge(byte[] vanilla, IReadOnlyList<byte[]> modVersions, bool isFpkd)
    {
        var order = new List<string>();                                  // inner paths, vanilla order first
        var data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var enc = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<string>();

        void Apply(byte[] fpkBytes)
        {
            var f = new FpkFile();
            using (var ms = new MemoryStream(fpkBytes, writable: false)) f.Read(ms);
            foreach (var e in f.Entries)
            {
                string p = e.FilePath.Data;
                if (!data.ContainsKey(p)) order.Add(p);
                data[p] = e.Data;
                enc[p] = e.Encrypted;
            }
            foreach (var r in f.References)
                if (!refs.Contains(r.Data)) refs.Add(r.Data);
        }

        if (vanilla != null) Apply(vanilla);
        foreach (var m in modVersions) Apply(m);

        var outF = new FpkFile();
        outF.SetType(isFpkd);
        foreach (var p in SortInner(order, isFpkd))
        {
            var e = new FpkEntry();
            e.FilePath.Data = p;
            e.Data = data[p];
            e.Loaded = true;          // use in-memory bytes, don't disk-load
            e.Encrypted = enc[p];
            outF.Entries.Add(e);
        }
        foreach (var r in refs)
            outF.References.Add(new FpkString { Data = r });

        using var os = new MemoryStream();
        outF.Write(os, "");           // baseDir unused (all entries Loaded)
        return os.ToArray();
    }

    // SnakeBite's fpk ordering: alpha (ascending for fpk, descending for fpkd),
    // then grouped by the engine's per-type order. fpkd load order matters.
    private static List<string> SortInner(List<string> files, bool isFpkd)
    {
        if (files.Count <= 1) return files;
        var sorted = new List<string>(files);
        if (isFpkd) sorted.Sort((a, b) => string.CompareOrdinal(b, a));
        else sorted.Sort(StringComparer.Ordinal);

        var order = isFpkd ? FpkdExt : FpkExt;
        var result = new List<string>(sorted.Count);
        foreach (var ext in order)
            foreach (var f in sorted)
            {
                int dot = f.LastIndexOf('.');
                if (dot >= 0 && string.Equals(f[(dot + 1)..], ext, StringComparison.OrdinalIgnoreCase))
                    result.Add(f);
            }
        // any extension not in the table keeps its sorted position at the end
        foreach (var f in sorted)
            if (!result.Contains(f)) result.Add(f);
        return result;
    }

    private static readonly string[] FpkExt =
    {
        "caar","fnt","atsh","frig","adm","frt","fpkl","fsm","ftdp","geobv","ftex","geoms",
        "gimr","gpfp","grxla","grxoc","htre","lba","lpsh","mog","mtar","nav2","nta","rdf",
        "ends","sand","mbl","tcvp","spch","trap","uigb","uilb","pcsp","tre2","fstb","twpf",
        "fv2t","fmdl","geom","gskl","fcnp","frdv","fdes","fclo","uif","uia","subp","sani",
        "ladb","frl","fv2","obr","lng2","mtard","obrb","dfrm","gani",
    };

    private static readonly string[] FpkdExt =
    {
        "fox2","evf","parts","vfxlb","vfx","vfxlf","veh","frld","des","bnd","tgt","phsd",
        "ph","sim","clo","fsd","sdf","lua","lng",
    };
}
