// GZ .g0s path hashing for mod files
using MgsvModBldr.Tools.G0s;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// GZ (.g0s) path hashing. Same Fox-Engine "/Assets/..." paths as TPP mods, but GZ
/// archives key entries by G0sHash (CityHash64 + GZ type-extension fold), NOT TPP's
/// PathCode. The gzs_dictionary uses the identical "/Assets/tpp/..." path strings, and
/// HashFileNameWithExtension strips the known GZ extension + folds its typeId, so a mod
/// file's path hashes straight to its .g0s entry hash.
/// </summary>
public static class GzHashing
{
    public static ulong NameToHash(string qarPath)
    {
        string p = "/" + qarPath.Replace('\\', '/').TrimStart('/');
        return G0sHash.HashFileNameWithExtension(p);
    }
}
