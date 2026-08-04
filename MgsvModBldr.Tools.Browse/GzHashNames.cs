// Ground Zeroes 48-bit asset ids -> readable paths
// 04/08/2026
using MgsvModBldr.Tools.G0s;
using MgsvModBldr.Tools.Qar;

namespace MgsvModBldr.Tools.Browse;

// GZ names an asset [ type id at bit 52 | 48-bit path hash ] — the same encoding a
// .g0s entry and an .mtar entry both use, so one resolver names both. The hash is
// G0sHash.HashFileName over the FULL "/Assets/…" path with the extension dropped.
// TPP's PathCode64 is a different function over the /Assets/-STRIPPED path; that one
// difference is why GZ ids never resolved against qar_dictionary.
//
// The index is built lazily over qar_dictionary's already-loaded text (hash -> blob
// token, no second copy) and dropped by QarNameDictionary.ClearAll.
public static class GzHashNames
{
    public const ulong NameMask = 0xFFFFFFFFFFFF;   // 48-bit
    private const int MaxTypeId = 106;              // highest id in G0sHash.TypeExtensions

    public static int TypeIdOf(ulong id) => (int)(id >> 52);

    // True when an id is GZ-encoded: a known type id at bit 52, bits 48..51 clear.
    // TPP tags a gani 8074 at bit 51 — far outside the table — so a TPP gani never
    // looks like a GZ one. A TPP id with a small ext code can slip through; it just
    // misses the lookup, and callers fall back to the TPP resolver.
    public static bool IsGzId(ulong id) =>
        ((id >> 48) & 0xF) == 0 && TypeIdOf(id) is > 0 and <= MaxTypeId;

    public static string ExtensionOf(ulong id)
    {
        int t = TypeIdOf(id);
        return t > 0 && t < G0sHash.TypeExtensions.Length ? G0sHash.TypeExtensions[t] : "";
    }

    // Full asset path ("/Assets/…/foo.gani"), or null when the id isn't GZ-encoded
    // or its name isn't in either dictionary.
    public static string ResolveFull(ulong id)
    {
        if (!IsGzId(id)) return null;

        var (index, dict) = Index();
        if (index is not null && index.TryGetValue(id & NameMask, out var token))
            return dict.PathOf(token) + ExtensionOf(id);

        // gzs_dictionary covers .g0s entries qar_dictionary doesn't list.
        return G0sHash.TryResolve(id, out var path) ? path : null;
    }

    public static string ResolveLeaf(ulong id)
    {
        var full = ResolveFull(id);
        if (full is null) return null;
        int i = full.LastIndexOfAny(_seps);
        return i >= 0 ? full[(i + 1)..] : full;
    }

    public static void Clear()
    {
        lock (_lock) { _index = null; _indexed = null; }
    }

    private static readonly char[] _seps = { '/', '\\' };
    private static readonly object _lock = new();
    private static Dictionary<ulong, long> _index;
    private static WeakReference<QarDictionary> _indexed;   // weak: never pins a dropped table

    // The index and the table its tokens point into, always as one pair.
    private static (Dictionary<ulong, long>, QarDictionary) Index()
    {
        var dict = QarNameDictionary.Get();
        if (dict is null) return (null, null);
        lock (_lock)
        {
            // Tokens are offsets into ONE table's blob; a reload invalidates them.
            if (_index is not null && _indexed.TryGetTarget(out var built) && ReferenceEquals(built, dict))
                return (_index, dict);

            var map = new Dictionary<ulong, long>(1 << 19);
            dict.ForEachPath((token, path) => map.TryAdd(G0sHash.HashFileName(path), token));
            _indexed = new WeakReference<QarDictionary>(dict);
            _index = map;
            return (map, dict);
        }
    }
}
