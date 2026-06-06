// Based on FoxKit Fox.FileSystem/Impl/PathCodeResolver.cs
using MgsvModBldr.Tools.GameHashing;

namespace MgsvModBldr.Tools.Qar;

public sealed class QarDictionary
{
    public const string DictionaryFileName = "qar_dictionary.txt";

    /// <summary>
    /// Resolve the external dictionary path: <c>qar_dictionary.txt</c>
    /// next to the running exe, falling back to the current directory.
    /// The dictionary is shipped as a loose file (not embedded) so it
    /// can be updated without recompiling.
    /// </summary>
    public static string DefaultDictionaryPath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, DictionaryFileName);
        if (File.Exists(beside)) return beside;
        return Path.Combine(Directory.GetCurrentDirectory(), DictionaryFileName);
    }

    private static readonly object Lock = new();
    private static QarDictionary? _cached;
    private static string? _cachedPath;

    private readonly Dictionary<ulong, string> _baseByHash;     // base-hash -> base path
    private readonly Dictionary<uint, string>  _extByCode;       // ext-code  -> ".ext"

    private QarDictionary(Dictionary<ulong, string> baseByHash, Dictionary<uint, string> extByCode)
    {
        _baseByHash = baseByHash;
        _extByCode  = extByCode;
    }

    public string Resolve(ulong pathHash, out bool found)
    {
        if (_baseByHash.TryGetValue(pathHash & GameHash.PATH_CODE_BASE_MASK, out var basePath))
        {
            found = true;
            uint extCode = GameHash.ExtCodeOf(pathHash);
            if (extCode != 0 && _extByCode.TryGetValue(extCode, out var ext))
                return basePath + ext;
            return basePath;
        }
        found = false;
        return $"{pathHash:x}";
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
            IEnumerable<string> lines = File.ReadAllLines(path);

            var baseByHash = new Dictionary<ulong, string>(capacity: 128 * 1024);
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0) continue;
                ulong key = GameHash.PathCode(line) & GameHash.PATH_CODE_BASE_MASK;
                baseByHash.TryAdd(key, line);
            }

            var extByCode = new Dictionary<uint, string>(KnownExtensions.Length);
            foreach (var ext in KnownExtensions)
                extByCode.TryAdd(GameHash.ExtensionCode(ext), ext);

            _cached     = new QarDictionary(baseByHash, extByCode);
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
