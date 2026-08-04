// Resolve mtar gani hashes to names (TPP + GZ)
using MgsvModBldr.Tools.GameHashing;

namespace MgsvModBldr.Tools.Index;

/// <summary>
/// Maps an mtar entry's 64-bit hash back to its gani path.
///
/// The entry hash is [ 13-bit extension code | 50-bit name hash ]. The name hash
/// is CityHash64WithSeeds over the path with the "/Assets/" prefix stripped and
/// the extension removed, masked to 50 bits — the same function for BOTH games.
/// Only the EXTENSION CODE differs: TPP tags a gani 8074, GZ tags it 22.
///
/// That difference is why GZ never resolved. Tools.Mtar's NameResolver derives its
/// lookup key by slicing the hash's HEX STRING at fixed offsets; `ToString("x")`
/// drops leading zeros, so TPP's 0xFC5… (16 chars) slices correctly while GZ's
/// 0x00B0… (14 chars) slices one character short — 0x…913362d2260 becomes
/// "13362d2260". Masking arithmetically instead works for any extension code.
/// </summary>
public static class MtarGaniNames
{
    /// <summary>TPP: extension code in the top 13 bits, name hash in the low 50.</summary>
    public const ulong NameMask = 0x3FFFFFFFFFFFF;

    /// <summary>
    /// GZ: extension code in the top 16 bits, name hash in the low 48. Measured, not
    /// assumed — bits 48..50 are zero across every entry of every GZ mtar sampled,
    /// while TPP fills them. GZ's gani code is 0x00B0; read under TPP's 13/50 split
    /// that same field reads as "22" (0xB0 >> 3), which is where that number came from.
    /// </summary>
    public const ulong GzNameMask = 0xFFFFFFFFFFFF;

    public static int ExtensionCode(ulong entryHash) => (int)(entryHash >> 51);

    /// <summary>
    /// GZ's type id — an INDEX into G0sHash.TypeExtensions, not a hash. 11 is .gani.
    /// It sits at bit 52, the same place a .g0s entry keeps it, so an mtar entry and
    /// an archive entry are encoded identically.
    /// </summary>
    public static int GzTypeId(ulong entryHash) => (int)(entryHash >> 52);

    /// <summary>Highest id in the GZ extension table; ids above this aren't GZ.</summary>
    private const int GzMaxTypeId = 106;

    /// <summary>
    /// True when the entry uses GZ's layout: a small type id at bit 52 and a 48-bit
    /// name, leaving bits 48..51 clear. TPP's gani code (8074, at bit 51) lands well
    /// outside the table, so the two never collide.
    /// </summary>
    public static bool IsGzLayout(ulong entryHash) => ((entryHash >> 48) & 0xF) == 0
                                                   && GzTypeId(entryHash) is > 0 and <= GzMaxTypeId;

    /// <summary>The lookup key: the name hash with the extension code discarded.</summary>
    public static ulong NameHash(ulong entryHash) =>
        IsGzLayout(entryHash) ? entryHash & GzNameMask : entryHash & NameMask;

    /// <summary>
    /// Hash a gani path the way the engine does. Accepts a full "/Assets/…" path
    /// with or without the .gani extension.
    /// </summary>
    public static ulong Hash(string path) => Hash(path, NameMask);

    /// <summary>
    /// GZ's name hash — a DIFFERENT function from TPP's, transcribed from
    /// stringid_raw_hash in MgsGroundZeroes.exe:
    ///
    ///   h    = CityHash64(str, len + 1)                 // buffer includes the NUL
    ///   seed = (sbyte)str[0] * 0x10000 + len            // first char and length
    ///   out  = HashLen16(h - K2, seed) & 0xFFFFFFFFFFFF // 48 bits
    ///
    /// The exe's constants say so directly: its multiplier -0x622015f714c7d297 is
    /// CityHash's kMul, and its addend +0x651e95c4d06fbfb1 is -K2, so the three-step
    /// mix at the end is exactly HashLen16. That makes it CityHash64WithSeeds with
    /// seed0 = K2 — the same call TPP uses, but seeded off the FRONT of the string
    /// rather than the last 8 characters reversed, and over a NUL-terminated buffer.
    /// </summary>
    public static ulong GzHash(string path)
    {
        // GZ hashes the FULL "/Assets/…" path, leading slash and all — only the
        // extension comes off. (TPP strips the /Assets/ prefix; GZ does not. This is
        // the same string form gzs_dictionary.txt is written in.)
        var text = path.Replace('\\', '/');
        int dot = text.LastIndexOf('.'), sl = text.LastIndexOf('/');
        if (dot > sl) text = text[..dot];
        if (text.Length == 0) return 0;

        // len + 1: the engine hashes the terminator too.
        var buf = new byte[text.Length + 1];
        for (int i = 0; i < text.Length; i++) buf[i] = (byte)text[i];

        ulong seed = unchecked((ulong)((long)(sbyte)buf[0] * 0x10000 + text.Length));
        return GameCityHash.CityHash64WithSeeds(buf, 0x9ae16a3b2f90404f, seed) & GzNameMask;
    }

    /// <summary>As <see cref="Hash(string)"/> but masked to the caller's field width
    /// (<see cref="NameMask"/> for TPP, <see cref="GzNameMask"/> for GZ).</summary>
    public static ulong Hash(string path, ulong mask)
    {
        var text = Normalise(path);
        const ulong seed0 = 0x9ae16a3b2f90404f;

        // seed1 is built from the LAST 8 characters, reversed.
        var seed1Bytes = new byte[sizeof(ulong)];
        for (int i = text.Length - 1, j = 0; i >= 0 && j < sizeof(ulong); i--, j++)
            seed1Bytes[j] = (byte)text[i];
        ulong seed1 = BitConverter.ToUInt64(seed1Bytes, 0);

        return GameCityHash.CityHash64WithSeeds(text.AsSpan(), seed0, seed1) & mask;
    }

    private const string AssetsPrefix = "/Assets/";

    private static string Normalise(string path)
    {
        var p = path.Replace('\\', '/');
        int dot = p.LastIndexOf('.');
        int slash = p.LastIndexOf('/');
        if (dot > slash) p = p[..dot];                       // drop the extension
        if (p.StartsWith(AssetsPrefix, StringComparison.OrdinalIgnoreCase))
            p = p[AssetsPrefix.Length..];
        else if (p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            p = p["Assets/".Length..];
        return p;
    }

    /// <summary>Build a name-hash → path map from a dictionary of gani paths.</summary>
    public static Dictionary<ulong, string> LoadDictionary(string dictionaryPath)
    {
        var map = new Dictionary<ulong, string>();
        if (!File.Exists(dictionaryPath)) return map;
        foreach (var line in File.ReadLines(dictionaryPath))
        {
            if (line.Length == 0) continue;
            map.TryAdd(Hash(line, NameMask), line);          // TPP key; first match wins
            map.TryAdd(GzHash(line), line);                  // GZ key (different function)
        }
        return map;
    }
}
