// Based on FoxKit Fox.Kernel/Hashing/Hashing.cs
using System.Globalization;

namespace MgsvModBldr.Tools.GameHashing;

public static class GameHash
{
    public const ulong STRING_ID_MASK            = 0xFFFFFFFFFFFF;
    public const ulong PATH_CODE_BASE_MASK       = 0x3FFFFFFFFFFFF;
    public const ulong PATH_CODE_EXT_MASK        = 0x1FFF;
    public const int   PATH_CODE_EXTENSION_OFFSET = 0x33;
    public const ulong PATH_CODE_USER_FLAG_MASK   = 0x4000000000000;
    public const ulong PATH_CODE_USER_FLAG_ANTIMASK = unchecked((ulong)-0x4000000000000 - 1);
    private const string ASSET_PATH = "/Assets/";

    private static ulong RawPathHashCode(ReadOnlySpan<char> path)
    {
        const ulong seed0 = 0x9ae16a3b2f90404f;
        ulong seed1 = 0;
        for (int i = path.Length - 1, j = 0; i >= 0 && j < sizeof(ulong); i--, j++)
            seed1 |= (ulong)path[i] << (j * 8);
        return GameCityHash.CityHash64WithSeeds(path, seed0, seed1);
    }

    private static ulong CityHashStrCodeInner(ReadOnlySpan<char> str)
    {
        const ulong seed0 = 0x9ae16a3b2f90404f;
        ulong seed1 = (ulong)(str.Length > 0 ? (str[0] << 16) + str.Length : 0);
        return GameCityHash.CityHash64WithSeeds(new string(str) + '\0', seed0, seed1) & STRING_ID_MASK;
    }

    private static uint FfPathExtCode(ReadOnlySpan<char> ext)
    {
        if (ext.Length > 0 && ext[0] == '.') ext = ext[1..];
        return (uint)(RawPathHashCode(ext) & PATH_CODE_EXT_MASK);
    }

    public static uint ExtensionCode(ReadOnlySpan<char> ext) => FfPathExtCode(ext);

    public static uint ExtCodeOf(ulong pathCode) => (uint)(pathCode >> PATH_CODE_EXTENSION_OFFSET);

    private static ulong PathHashCode(ReadOnlySpan<char> path)
    {
        ReadOnlySpan<char> pathSpan = path;

        int finalBaseSegmentIndex = pathSpan.LastIndexOf('/');
        if (finalBaseSegmentIndex == -1)
        {
            finalBaseSegmentIndex = pathSpan.LastIndexOf('\\');
            if (finalBaseSegmentIndex == -1) finalBaseSegmentIndex = 0;
        }

        ReadOnlySpan<char> finalSegment = pathSpan[finalBaseSegmentIndex..];

        int extIndex = finalSegment.IndexOf('.');
        if (extIndex == -1) extIndex = path.Length;
        else extIndex = finalBaseSegmentIndex + extIndex;

        ReadOnlySpan<char> extSpan = path[extIndex..];
        pathSpan = path[..^extSpan.Length];

        if (path.StartsWith(ASSET_PATH)) pathSpan = pathSpan[ASSET_PATH.Length..];

        ulong baseHash = RawPathHashCode(pathSpan) & PATH_CODE_BASE_MASK;
        ulong extensionHash = (ulong)FfPathExtCode(extSpan) << PATH_CODE_EXTENSION_OFFSET;
        return extensionHash | baseHash;
    }

    private static ReadOnlySpan<char> StripLeadingSlash(ReadOnlySpan<char> s)
        => s.Length > 0 && s[0] == '/' ? s[1..] : s;

    private static ulong SetUserFlag(ulong hash, int userFlag)
    {
        ulong mask = unchecked((ulong)(userFlag == 0 ? 0L : -1L));
        return (mask & PATH_CODE_USER_FLAG_MASK) | (hash & PATH_CODE_USER_FLAG_ANTIMASK);
    }

    private static readonly string[] RuntimeProjectList = { "fox", "fox_export", "tpp", "sh", "mgo" };

    private static bool IsImplicitRuntimeProject(ReadOnlySpan<char> path)
    {
        int assetsIndex = path.IndexOf(ASSET_PATH, StringComparison.Ordinal);
        if (assetsIndex == -1) return false;

        path = path[ASSET_PATH.Length..];
        int nextSlash = path.IndexOf('/');
        if (nextSlash == -1) return false;
        path = path[..nextSlash];

        foreach (var project in RuntimeProjectList)
            if (path.Equals(project, StringComparison.Ordinal)) return true;
        return false;
    }

    private static ulong PathCodeInner(ReadOnlySpan<char> path)
    {
        ulong code;
        if (path.StartsWith(ASSET_PATH))
        {
            code = PathHashCode(path);
            return IsImplicitRuntimeProject(path) ? code : SetUserFlag(code, 1);
        }

        if (path.Length == 0) return 0;

        bool mustReallocate = path.IndexOf('\\') != -1;
        if (mustReallocate)
        {
            string modified = new string(path).Replace('\\', '/');
            ReadOnlySpan<char> span = modified.AsSpan();
            int starterPrefix = modified.IndexOf("./", StringComparison.Ordinal);
            if (starterPrefix != -1) span = span[2..];
            span = StripLeadingSlash(span);
            code = PathHashCode(span);
            return SetUserFlag(code, 1);
        }
        else
        {
            int starterPrefix = path.IndexOf("./", StringComparison.Ordinal);
            if (starterPrefix != -1) path = path[2..];
            path = StripLeadingSlash(path);
            code = PathHashCode(path);
            return SetUserFlag(code, 1);
        }
    }

    public static ulong PathCode(ReadOnlySpan<char> str)
    {
        if (str.Length > 2 && str.StartsWith("0x"))
        {
            if (ulong.TryParse(str[2..], NumberStyles.HexNumber, null, out ulong raw)) return raw;
        }
        return PathCodeInner(str);
    }

    public static ulong StringId(ReadOnlySpan<char> str)
    {
        if (str.Length > 2 && str.StartsWith("0x"))
        {
            if (ulong.TryParse(str[2..], NumberStyles.HexNumber, null, out ulong raw)) return raw;
        }
        return CityHashStrCodeInner(str);
    }
}
