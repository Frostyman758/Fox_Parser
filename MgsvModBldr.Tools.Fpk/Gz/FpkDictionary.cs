// MD5-keyed path dictionary for GZ fpk entry name resolution. fpk_dictionary.txt
// is one full path per line; the key is MD5(path). Mirrors GzsTool 0.2's
// Hashing.ReadMd5Dictionary. GZ-only — the TPP fpk path never needs it.
//
// DictionaryDirectory lets a host whose AppContext.BaseDirectory isn't its own
// folder (the NativeAOT bridge loaded into explorer.exe) point at the right
// location; null (default) uses AppContext.BaseDirectory.
using System.Security.Cryptography;
using System.Text;

namespace MgsvModBldr.Tools.Fpk.Gz;

public static class FpkDictionary
{
    public const string DictionaryFileName = "fpk_dictionary.txt";

    private static readonly object Lock = new();
    private static Dictionary<byte[], string> _dict;
    private static string _dictDir;

    public static string DictionaryDirectory
    {
        get => _dictDir;
        set { lock (Lock) { _dictDir = value; _dict = null; } }
    }

    public static bool TryResolve(byte[] md5Hash, out string path)
        => Dict().TryGetValue(md5Hash, out path);

    private static Dictionary<byte[], string> Dict()
    {
        if (_dict is not null) return _dict;
        lock (Lock)
        {
            if (_dict is not null) return _dict;
            var map = new Dictionary<byte[], string>(Md5Comparer.Instance);
            var path = ResolveDict(DictionaryFileName);
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path))
                {
                    var h = MD5.HashData(Encoding.UTF8.GetBytes(line));
                    if (!map.ContainsKey(h)) map[h] = line; // first match wins
                }
            _dict = map;
            return _dict;
        }
    }

    private static string ResolveDict(string name)
    {
        var baseDir = _dictDir ?? AppContext.BaseDirectory;
        var inDict = Path.Combine(baseDir, "dict", name);
        return File.Exists(inDict) ? inDict : Path.Combine(baseDir, name);
    }

    private sealed class Md5Comparer : IEqualityComparer<byte[]>
    {
        public static readonly Md5Comparer Instance = new();
        public bool Equals(byte[] a, byte[] b)
            => a is not null && b is not null && a.AsSpan().SequenceEqual(b);
        public int GetHashCode(byte[] x)
            => x.Length >= 4 ? BitConverter.ToInt32(x, 0) : x.Length;
    }
}
