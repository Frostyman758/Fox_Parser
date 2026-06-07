// Based on MtarTool.Core/Utility/NameResolver.cs
// Changes from the original: CityHash -> Core.CityHash64; dictionary
// loaded tolerantly from AppContext.BaseDirectory (empty if absent, so
// the tool never crashes — unresolved hashes round-trip as hex); the
// linear hash search is replaced with an O(1) lookup (first entry wins,
// same as the original's first-match); diagnostic Console output and the
// hashed_names.txt side file are dropped (they do not affect output).
using System;
using System.Collections.Generic;
using System.IO;
using CityHash = MgsvModBldr.Core.CityHash64;

namespace MgsvModBldr.Tools.Mtar.Utility
{
    public static class NameResolver
    {
        private const string ASSETS_CONST = "/Assets/";

        private static readonly Dictionary<string, string> hashToName = LoadDictionary();

        private static Dictionary<string, string> LoadDictionary()
        {
            var map = new Dictionary<string, string>();
            var path = Path.Combine(AppContext.BaseDirectory, "mtar_dictionary.txt");
            if (!File.Exists(path)) return map;
            foreach (var line in File.ReadAllLines(path))
            {
                var h = GetHashFromString(line);
                if (!map.ContainsKey(h)) map[h] = line; // first match wins
            }
            return map;
        }

        public static string GetExtension(ulong hash)
        {
            ulong hashExtension = hash >> 51;
            switch (hashExtension)
            {
                case 8074: return "gani";
            }
            return "Unknown!";
        }

        public static string GetHashFromULong(ulong ul)
        {
            string hash = ul.ToString("x");

            string prefix = "";

            switch (hash.Substring(2, 2))
            {
                case "51":
                    prefix = "1";
                    break;
                case "52":
                    prefix = "2";
                    break;
                case "53":
                    prefix = "3";
                    break;
            }

            if (hash.Substring(3)[0] == '0' && hash.Substring(4)[0] == '0')
            {
                return hash.Substring(5);
            }

            return prefix + hash.Substring(4);
        }

        public static string GetHashFromString(string text)
        {
            string toHash = text;

            if (text.Contains(ASSETS_CONST))
            {
                toHash = text.Substring(ASSETS_CONST.Length);
            }

            return GetStrCode32(toHash).ToString("x");
        }

        public static string TryFindName(string text)
        {
            return hashToName.TryGetValue(text, out var name) ? name : text;
        }

        public static ulong GetHashFromName(string text)
        {
            string ganiPath = Path.GetDirectoryName(text).Replace('\\', '/');
            string ganiName = Path.GetFileNameWithoutExtension(text);

            if (char.IsDigit(ganiName[0]) && ganiName[4] == '_')
            {
                ganiName = ganiName.Substring(5);
            }

            if (ganiPath != "")
            {
                ganiPath += "/";
            }

            text = ganiPath + ganiName;

            if (text.Contains(ASSETS_CONST))
            {
                text = GetHashFromString(text);
            }

            string outputText = "";
            ulong outputULong = 0x0;

            while (text.Length < 13)
            {
                text = "0" + text;
            }

            switch (text[0])
            {
                case '0': outputText = "FC50" + text.Substring(1);
                    break;
                case '1': outputText = "FC51" + text.Substring(1);
                    break;
                case '2': outputText = "FC52" + text.Substring(1);
                    break;
                case '3': outputText = "FC53" + text.Substring(1);
                    break;
            }

            outputULong = Convert.ToUInt64(outputText, 16);

            return outputULong;
        }

        private static ulong GetStrCode32(string text)
        {
            const ulong seed0 = 0x9ae16a3b2f90404f;
            byte[] seed1Bytes = new byte[sizeof(ulong)];
            for (int i = text.Length - 1, j = 0; i >= 0 && j < sizeof(ulong); i--, j++)
            {
                seed1Bytes[j] = Convert.ToByte(text[i]);
            }
            ulong seed1 = BitConverter.ToUInt64(seed1Bytes, 0);
            ulong maskedHash = CityHash.CityHash64WithSeeds(text, seed0, seed1) & 0x3FFFFFFFFFFFF;
            return maskedHash;
        }
    }
}
