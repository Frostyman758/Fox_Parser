using MgsvModBldr.Core;

namespace MgsvModBldr.Tools.G0s;

/// <summary>
/// GZ QAR path hashing + filename resolution, ported from GzsTool 0.2
/// (Utility/Hashing.cs). Uses the legacy CityHash (Core.CityHash64 — the
/// same variant as the CityHash.Net.Legacy DLL GzsTool shipped) so hashes
/// match the game byte-for-byte. The GZ type-extension table differs from
/// TPP's, hence its own copy here. The dictionary (gzs_dictionary.txt) maps
/// the 48-bit path hash back to the extension-less path.
/// </summary>
public static class G0sHash
{
    // GZ extension table (typeId -> extension). typeId is stored in hash>>52.
    public static readonly string[] TypeExtensions =
    {
        "", ".xml", ".json", ".ese", ".fxp", ".fpk", ".fpkd", ".fpkl", ".aib", ".frig",
        ".mtar", ".gani", ".evb", ".evf", ".ag.evf", ".cc.evf", ".fx.evf", ".sd.evf", ".vo.evf", ".fsd",
        ".fage", ".fago", ".fag", ".fagx", ".fagp", ".frdv", ".fdmg", ".des", ".fdes", ".aibc",
        ".mtl", ".fsml", ".fox", ".fox2", ".las", ".fstb", ".lua", ".fcnp", ".fcnpx", ".sub",
        ".fova", ".lad", ".lani", ".vfx", ".vfxbin", ".frt", ".gpfp", ".gskl", ".geom", ".tgt",
        ".path", ".fmdl", ".ftex", ".htre", ".tre2", ".grxla", ".grxoc", ".mog", ".pftxs", ".nav2",
        ".bnd", ".parts", ".phsd", ".ph", ".veh", ".sdf", ".sad", ".sim", ".fclo", ".clo",
        ".lng", ".uig", ".uil", ".uif", ".uia", ".fnt", ".utxl", ".uigb", ".vfxdb", ".rbs",
        ".aia", ".aim", ".aip", ".aigc", ".aig", ".ait", ".fsm", ".obr", ".obrb", ".lpsh",
        ".sani", ".rdb", ".phep", ".simep", ".atsh", ".txt", ".1.ftexs", ".2.ftexs", ".3.ftexs", ".4.ftexs",
        ".5.ftexs", ".sbp", ".mas", ".rdf", ".wem", ".lba", ".uilb",
    };

    public const ulong HashMask = 0xFFFFFFFFFFFF; // 48-bit

    /// <summary>HashFileName: CityHash64 of the extension-less path (+NUL), seeded by length/first char, masked to 48 bits.</summary>
    public static ulong HashFileName(string text)
    {
        const ulong seed0 = 0x9ae16a3b2f90404fUL;
        ulong seed1 = text.Length > 0 ? (uint)(text[0] << 16) + (uint)text.Length : 0;
        return CityHash64.CityHash64WithSeeds(text + "\0", seed0, seed1) & HashMask;
    }

    /// <summary>Full hash for a path WITH extension: hashes the stem, then folds the typeId into bits 52+.</summary>
    public static ulong HashFileNameWithExtension(string filePath)
    {
        int typeId = 0;
        string hashablePart = filePath;
        // The reference requires exactly one matching known extension.
        int match = -1;
        for (int id = 1; id < TypeExtensions.Length; id++)
        {
            if (filePath.EndsWith(TypeExtensions[id], StringComparison.InvariantCultureIgnoreCase))
            {
                if (match != -1) { match = -2; break; } // ambiguous -> treat as none (reference: Count()==1)
                match = id;
            }
        }
        if (match > 0)
        {
            typeId = match;
            int extIndex = filePath.LastIndexOf(TypeExtensions[match], StringComparison.InvariantCultureIgnoreCase);
            hashablePart = filePath.Substring(0, extIndex);
        }
        ulong hash = HashFileName(hashablePart);
        return hash + ((ulong)typeId << 52);
    }

    // ─── dictionary (hash -> extension-less path) ───────────────────────────

    private static readonly object Lock = new();
    private static Dictionary<ulong, string> _dict;
    private static string _dictDir;

    /// <summary>
    /// Explicit directory to look for gzs_dictionary.txt in. Hosts whose
    /// <see cref="AppContext.BaseDirectory"/> isn't their own folder — e.g. a
    /// NativeAOT library loaded into another process (explorer.exe) — set this
    /// so the dictionary is still found. Null (default) uses
    /// AppContext.BaseDirectory, preserving the original behaviour. Setting it
    /// drops the cached dictionary so the next lookup reloads.
    /// </summary>
    public static string DictionaryDirectory
    {
        get => _dictDir;
        set { lock (Lock) { _dictDir = value; _dict = null; } }
    }

    private static Dictionary<ulong, string> Dict()
    {
        if (_dict is not null) return _dict;
        lock (Lock)
        {
            if (_dict is not null) return _dict;
            var map = new Dictionary<ulong, string>();
            var path = ResolveDict("gzs_dictionary.txt");
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path))
                {
                    var h = HashFileName(line);
                    if (!map.ContainsKey(h)) map[h] = line; // first match wins (reference)
                }
            _dict = map;
            return _dict;
        }
    }

    private static string ResolveDict(string name)
    {
        var baseDir = _dictDir ?? AppContext.BaseDirectory;
        var inDict = Path.Combine(baseDir, "dict", name);
        return File.Exists(inDict) ? inDict : Path.Combine(baseDir, name);
    }

    /// <summary>
    /// Resolve a 64-bit entry hash to its on-disk file path (with extension).
    /// Mirrors GzsTool 0.2 TryGetFileNameFromHash: dictionary stem (or hex of
    /// the 48-bit hash) + the typeId's extension. Returns whether the name was
    /// found in the dictionary.
    /// </summary>
    public static bool TryResolve(ulong hash, out string filePath)
    {
        int extId = (int)(hash >> 52 & 0xFFFF);
        string ext = extId >= 0 && extId < TypeExtensions.Length ? TypeExtensions[extId] : "";
        ulong masked = hash & HashMask;
        bool found = Dict().TryGetValue(masked, out var stem);
        if (!found) stem = string.Format("{0:x}", masked);
        filePath = stem + ext;
        return found;
    }
}
