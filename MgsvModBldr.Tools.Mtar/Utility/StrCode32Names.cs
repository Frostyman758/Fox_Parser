// resolve StrCode32 ids to their real names for the mtar XML
using System;
using System.Collections.Generic;
using System.IO;
using MgsvModBldr.Tools.GameHashing;

namespace MgsvModBldr.Tools.Mtar.Utility
{
    /// <summary>
    /// Bone and motion-point ids are StrCode32, and both vocabularies ship as dictionaries, so the
    /// XML can carry `SKL_023_RHAND` instead of `38b1433c`. Writing accepts either: anything that
    /// is not exactly 8 hex digits is hashed, so an edited file round-trips byte for byte.
    /// </summary>
    public static class StrCode32Names
    {
        private static Dictionary<uint, string> _map;

        private static Dictionary<uint, string> Map
        {
            get
            {
                if (_map is not null) return _map;
                _map = new Dictionary<uint, string>();
                foreach (var name in new[] { "bone_dictionary.txt", "motionpoint_dictionary.txt" })
                {
                    var path = Path.Combine(AppContext.BaseDirectory, "dict", name);
                    if (!File.Exists(path)) continue;
                    foreach (var line in File.ReadLines(path))
                    {
                        var s = line.Trim();
                        if (s.Length == 0) continue;
                        _map[(uint)GameHash.StringId(s)] = s;
                    }
                }
                return _map;
            }
        }

        /// <summary>The name if the dictionaries know it, else the bare hash.</summary>
        public static string Text(uint hash) =>
            Map.TryGetValue(hash, out var n) ? n : hash.ToString("x8");

        /// <summary>Inverse of <see cref="Text"/> — 8 hex digits stay literal, anything else hashes.</summary>
        public static uint Value(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            if (text.Length == 8 && uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                                                  null, out var raw)) return raw;
            return (uint)GameHash.StringId(text);
        }
    }
}
