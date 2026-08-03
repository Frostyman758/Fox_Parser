// QAR path hashing for mod files
using System.Globalization;
using MgsvModBldr.Tools.GameHashing;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>FoxEngine path hashing (mirrors SnakeBite's Tools.NameToHash, on GameHash).</summary>
public static class Hashing
{
    public static string ToQarPath(string path) => "/" + path.Replace('\\', '/').TrimStart('/');

    public static ulong NameToHash(string fileName)
    {
        string filePath = ToQarPath(fileName);
        ulong hash = GameHash.PathCode(filePath);

        // hashed names live in the archive root (e.g. "a1b2c3d4.lua")
        if (!filePath.Substring(1).Contains("/"))
        {
            string fn = filePath.TrimStart('/');
            int dot = fn.IndexOf('.');
            if (dot >= 0)
            {
                string noExt = fn.Substring(0, dot);
                string ext = fn.Substring(dot + 1);
                if (ulong.TryParse(noExt, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash))
                {
                    ulong extHash = (ulong)GameHash.ExtensionCode(ext) & 0x1FFF;
                    hash = (extHash << 51) | hash;
                }
                else
                {
                    hash = GameHash.PathCode(filePath);
                }
            }
        }
        return hash;
    }
}
