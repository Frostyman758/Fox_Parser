// Based on FoxKit Fox.FileSystem/Impl/PathCodeResolver.cs
using MgsvModBldr.Tools.GameHashing;

namespace MgsvModBldr.Tools.Qar;

public sealed class QarDictionary
{
    public const string DictionaryFileName = "qar_dictionary.txt";

    public static string DefaultDictionaryPath() => ResolveDict(DictionaryFileName);

    internal static string ResolveDict(string fileName)
    {
        foreach (var p in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "dict", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "dict", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), fileName),
        })
            if (File.Exists(p)) return p;
        return Path.Combine(AppContext.BaseDirectory, "dict", fileName);
    }

    private static readonly object Lock = new();
    private static QarDictionary? _cached;
    private static string? _cachedPath;

    // Release the process-wide cached table (idle memory reclaim). The next
    // Load() re-parses from disk.
    public static void DropCache()
    {
        lock (Lock) { _cached = null; _cachedPath = null; }
    }

    // Lines live as ONE UTF-8 blob + hash -> (offset<<24 | length) index — about
    // half the resident cost of a per-line string table (~600k paths). Only a
    // resolve HIT materialises a string.
    private readonly byte[] _blob;
    private readonly Dictionary<ulong, long> _baseByHash;       // base-hash -> packed blob region
    private readonly Dictionary<uint, string> _extByCode;        // ext-code  -> ".ext"

    private QarDictionary(byte[] blob, Dictionary<ulong, long> baseByHash, Dictionary<uint, string> extByCode)
    {
        _blob       = blob;
        _baseByHash = baseByHash;
        _extByCode  = extByCode;
    }

    public string Resolve(ulong pathHash, out bool found)
    {
        if (_baseByHash.TryGetValue(pathHash & GameHash.PATH_CODE_BASE_MASK, out var packed))
        {
            found = true;
            var basePath = System.Text.Encoding.UTF8.GetString(_blob, (int)(packed >> 24), (int)(packed & 0xFFFFFF));
            uint extCode = GameHash.ExtCodeOf(pathHash);
            if (extCode != 0 && _extByCode.TryGetValue(extCode, out var ext))
                return basePath + ext;
            return basePath;
        }
        found = false;
        return $"{pathHash:x}";
    }

    private static Dictionary<uint, string>? _extMap;

    public static string? ExtensionFor(uint extCode)
    {
        if (_extMap is null)
        {
            var m = new Dictionary<uint, string>(KnownExtensions.Length);
            foreach (var ext in KnownExtensions)
                m.TryAdd(GameHash.ExtensionCode(ext), ext);
            _extMap = m;
        }
        return _extMap.TryGetValue(extCode, out var e) ? e : null;
    }

    public static QarDictionary Load(string? path = null)
    {
        path ??= DefaultDictionaryPath();
        lock (Lock)
        {
            if (_cached is not null && _cachedPath == path) return _cached;

            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"QAR dictionary not found: {path}. Ship {DictionaryFileName} next to the executable.", path);

            // Streamed line-by-line (no ReadAllLines double-buffer); line strings
            // are transient — retained lines land in the blob as UTF-8 bytes.
            var baseByHash = new Dictionary<ulong, long>(capacity: 1 << 20);
            using var blob = new MemoryStream(checked((int)new FileInfo(path).Length));
            using (var sr = new StreamReader(path))
            {
                string? raw;
                while ((raw = sr.ReadLine()) is not null)
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    ulong key = GameHash.PathCode(line) & GameHash.PATH_CODE_BASE_MASK;
                    var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                    if (bytes.Length > 0xFFFFFF) throw new InvalidDataException("dictionary line too long");
                    if (baseByHash.TryAdd(key, ((long)blob.Position << 24) | (uint)bytes.Length))
                        blob.Write(bytes, 0, bytes.Length);
                }
            }

            var extByCode = new Dictionary<uint, string>(KnownExtensions.Length);
            foreach (var ext in KnownExtensions)
                extByCode.TryAdd(GameHash.ExtensionCode(ext), ext);

            _cached     = new QarDictionary(blob.ToArray(), baseByHash, extByCode);
            _cachedPath = path;
            return _cached;
        }
    }

    private static readonly string[] KnownExtensions =
    {
        ".1.ftexs", ".2.ftexs", ".3.ftexs", ".4.ftexs", ".5.ftexs", ".6.ftexs",
        ".ag.evf", ".aia", ".aib", ".aibc", ".aig", ".aigc", ".ladb", ".aim",
        ".aip", ".ait", ".atsh", ".bnd", ".cc.evf", ".clo", ".des", ".ese",
        ".evb", ".evf", ".fag", ".fage", ".fago", ".fagp", ".fagx", ".fclo",
        ".fcnp", ".fcnpx", ".fdes", ".fdmg", ".fmdl", ".fnt", ".fova", ".fox",
        ".fox2", ".fpk", ".fpkd", ".fpkl", ".frdv", ".frig", ".frt", ".fsd",
        ".fsm", ".fsml", ".fstb", ".ftex", ".fx.evf", ".fxp", ".gani", ".geom",
        ".gpfp", ".grxla", ".grxoc", ".gskl", ".htre", ".json", ".lad", ".lani",
        ".las", ".lba", ".lng", ".eng.lng", ".jpn.lng", ".fre.lng", ".ita.lng",
        ".ger.lng", ".spa.lng", ".por.lng", ".rus.lng", ".lpsh", ".lua", ".mas",
        ".mog", ".mtar", ".mtl", ".nav2", ".obr", ".obrb", ".parts", ".path",
        ".pftxs", ".ph", ".phep", ".phsd", ".rbs", ".rdb", ".rdf", ".sad",
        ".sani", ".sbp", ".spch", ".sd.evf", ".sdf", ".sim", ".simep", ".sub",
        ".subp", ".tgt", ".tre2", ".txt", ".uia", ".uif", ".uig", ".uigb",
        ".uil", ".uilb", ".utxl", ".veh", ".vfx", ".vfxbin", ".vfxdb", ".vo.evf",
        ".wem", ".xml", ".ffnt", ".fsop", ".fv2", ".1.nav2", ".csnav", ".vnav",
        ".dnav", ".snav", ".rnav", ".info", ".fmdlb", ".dnav2", ".mbl", ".sand",
        ".qar", ".nta", "vpc", "twss", "tmss", "adm", "tetl", "tmsl", "tmsu",
        "tmsf", "twpf", "cani", ".bnk", ".fmtt", ".wmv",
    };
}
