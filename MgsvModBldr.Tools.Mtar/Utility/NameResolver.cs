// Based on MtarTool.Core/Utility/NameResolver.cs
// Changes from the original: CityHash -> GameHashing.GameCityHash; dictionary
// loaded tolerantly from AppContext.BaseDirectory (empty if absent, so
// the tool never crashes — unresolved hashes round-trip as hex); the
// linear hash search is replaced with an O(1) lookup (first entry wins,
// same as the original's first-match); diagnostic Console output and the
// hashed_names.txt side file are dropped (they do not affect output).
//
// 03/08/2026 — GZ SUPPORT + ARITHMETIC KEYS. The original keyed everything off
// hash.ToString("x") and sliced that string at FIXED offsets. That silently
// mangles Ground Zeroes: GZ hashes are 14 hex chars (0x00b0…) where TPP's are 16
// (0xfc50…), so the slice ran one character short and 0x00b00913362d2260 became
// "13362d2260" — a name that reconstructs on repack as 0xFC500013362d2260, i.e.
// GZ mtars could be unpacked but NEVER repacked without corrupting every entry.
//
// The two games hash differently, and the container TYPE does not tell you which
// (TPP ships type-1 mtars — TppRaven_layers, Ocelot2_facial — that use TPP
// hashing, while every GZ mtar is type 1 with GZ hashing). So the flavour is
// detected from the HASH ITSELF on read and carried on the archive root for write.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CityHash = MgsvModBldr.Tools.GameHashing.GameCityHash;

namespace MgsvModBldr.Tools.Mtar.Utility
{
    public static class NameResolver
    {
        private const string ASSETS_CONST = "/Assets/";

        /// <summary>TPP: 13-bit extension code above a 50-bit name hash. 8074 = .gani.</summary>
        private const ulong TPP_NAME_MASK = 0x3FFFFFFFFFFFF;
        private const ulong TPP_GANI_EXT = 8074;

        /// <summary>GZ: type id at bit 52 above a 48-bit name hash. 11 = .gani.</summary>
        private const ulong GZ_NAME_MASK = 0xFFFFFFFFFFFF;
        private const ulong GZ_GANI_TYPE = 11;
        private const ulong GZ_MAX_TYPE = 106;   // highest id in the GZ extension table

        // Key -> path, built for BOTH flavours so one dictionary of plain paths names
        // TPP and GZ entries alike. Key format is per-flavour; see GetHashFromULong.
        private static readonly Dictionary<string, string> hashToName = LoadDictionary();

        private static string ResolveDict(string name)
        {
            var inDict = Path.Combine(AppContext.BaseDirectory, "dict", name);
            return File.Exists(inDict) ? inDict : Path.Combine(AppContext.BaseDirectory, name);
        }

        private static Dictionary<string, string> LoadDictionary()
        {
            var map = new Dictionary<string, string>();
            var path = ResolveDict("mtar_dictionary.txt");
            if (!File.Exists(path)) return map;
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Length == 0) continue;
                var tpp = GetHashFromString(line);                                // legacy key format
                var gz = ((GZ_GANI_TYPE << 52) | GetGzCode(line)).ToString("x16"); // full hash
                if (!map.ContainsKey(tpp)) map[tpp] = line;   // first match wins
                if (!map.ContainsKey(gz)) map[gz] = line;
            }
            return map;
        }

        /// <summary>True when the hash uses GZ's layout (small type id at bit 52).</summary>
        public static bool IsGzHash(ulong hash)
        {
            ulong typeId = hash >> 52;
            return ((hash >> 48) & 0xF) == 0 && typeId > 0 && typeId <= GZ_MAX_TYPE;
        }

        public static string GetExtension(ulong hash)
        {
            if (IsGzHash(hash)) return hash >> 52 == GZ_GANI_TYPE ? "gani" : "Unknown!";
            return hash >> 51 == TPP_GANI_EXT ? "gani" : "Unknown!";
        }

        /// <summary>
        /// The dictionary key for an entry hash.
        ///
        /// TPP keeps the ORIGINAL key format exactly — it is already lossless there
        /// (the FC50..FC53 nibble is carried as a leading 0..3 digit and rebuilt by
        /// GetHashFromName), and matching it byte-for-byte keeps our xml identical to
        /// the reference tool's. GZ, which the original mangles, uses the full 16-digit
        /// hash so its bits survive a repack.
        /// </summary>
        public static string GetHashFromULong(ulong ul)
        {
            if (IsGzHash(ul)) return ul.ToString("x16");

            string hash = ul.ToString("x");
            if (hash.Length < 5) return hash;              // degenerate (e.g. a 0 hash)

            string prefix = hash.Substring(2, 2) switch
            {
                "51" => "1",
                "52" => "2",
                "53" => "3",
                _ => "",
            };

            if (hash[3] == '0' && hash[4] == '0') return hash.Substring(5);
            return prefix + hash.Substring(4);
        }

        public static string GetHashFromString(string text) =>
            GetStrCode32(StripAssets(text)).ToString("x");

        public static string TryFindName(string text) =>
            hashToName.TryGetValue(text, out var name) ? name : text;

        /// <summary>
        /// Name -> entry hash. <paramref name="gz"/> picks the flavour; it comes from
        /// the archive root, which recorded what its entries actually were on read.
        /// A name that is just a 16-digit hex hash (an unresolved entry) is parsed
        /// straight back, so unresolved entries round-trip exactly either way.
        /// </summary>
        public static ulong GetHashFromName(string text, bool gz = false)
        {
            string ganiPath = Path.GetDirectoryName(text)?.Replace('\\', '/') ?? "";
            string ganiName = Path.GetFileNameWithoutExtension(text);

            // MtarTool's numbered-unpack prefix ("0001_name") is not part of the path.
            if (ganiName.Length > 5 && char.IsDigit(ganiName[0]) && ganiName[4] == '_')
                ganiName = ganiName.Substring(5);

            if (ganiPath.Length > 0) ganiPath += "/";
            text = ganiPath + ganiName;

            if (gz)
            {
                // An unresolved GZ entry is its own 16-digit hash: keep the bits.
                if (ganiPath.Length == 0 && ganiName.Length == 16
                    && ulong.TryParse(ganiName, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
                    return raw;
                return (GZ_GANI_TYPE << 52) | GetGzCode(text);
            }

            // ── TPP: the original reconstruction, unchanged ──
            if (text.Contains(ASSETS_CONST)) text = GetHashFromString(text);

            while (text.Length < 13) text = "0" + text;

            string outputText = text[0] switch
            {
                '0' => "FC50" + text.Substring(1),
                '1' => "FC51" + text.Substring(1),
                '2' => "FC52" + text.Substring(1),
                '3' => "FC53" + text.Substring(1),
                _ => "",
            };

            return outputText.Length == 0 ? 0 : Convert.ToUInt64(outputText, 16);
        }

        private static string StripAssets(string text) =>
            text.Contains(ASSETS_CONST) ? text.Substring(text.IndexOf(ASSETS_CONST, StringComparison.Ordinal) + ASSETS_CONST.Length) : text;

        /// <summary>TPP: /Assets/ stripped, seed from the LAST 8 chars reversed, 50-bit.</summary>
        private static ulong GetStrCode32(string text)
        {
            const ulong seed0 = 0x9ae16a3b2f90404f;
            byte[] seed1Bytes = new byte[sizeof(ulong)];
            for (int i = text.Length - 1, j = 0; i >= 0 && j < sizeof(ulong); i--, j++)
                seed1Bytes[j] = (byte)text[i];
            ulong seed1 = BitConverter.ToUInt64(seed1Bytes, 0);
            return CityHash.CityHash64WithSeeds(text, seed0, seed1) & TPP_NAME_MASK;
        }

        /// <summary>
        /// GZ: the FULL "/Assets/…" path (NOT stripped), hashed WITH its NUL
        /// terminator, seeded from the FIRST char and the length, masked to 48 bits.
        /// Transcribed from stringid_raw_hash in MgsGroundZeroes.exe; identical to
        /// G0sHash.HashFileName, which is how .g0s entries are named.
        /// </summary>
        private static ulong GetGzCode(string text)
        {
            const ulong seed0 = 0x9ae16a3b2f90404f;
            if (text.Length == 0) return 0;
            ulong seed1 = (ulong)((long)(sbyte)text[0] * 0x10000 + text.Length);
            return CityHash.CityHash64WithSeeds(text + "\0", seed0, seed1) & GZ_NAME_MASK;
        }
    }
}
