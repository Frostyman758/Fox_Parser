using System.Diagnostics;
using System.Security.Cryptography;

namespace MgsvModBldr.Tools.Testing;

/// <summary>
/// Generic, tool-agnostic test utilities: per-file parallel gate
/// running with stable output order, byte/content tree comparison, temp
/// dirs, hashing and size formatting. Tool-specific gates live in each
/// tool's .Tests project and call into these.
/// </summary>
public static class TestHelpers
{
    /// <summary>
    /// Per-file gate runner. Each case is independent (its own scratch
    /// dir, no shared state) so Parallel.For is safe. Results are
    /// collected into a position-indexed array so the printed output is
    /// in stable input order, not completion order — diff-friendly logs.
    /// </summary>
    public static (int pass, int fail) RunParallel(
        List<string> samples,
        Func<string, (bool ok, string note)> gate)
    {
        var results = new (bool ok, string note)[samples.Count];
        Parallel.For(0, samples.Count, i =>
        {
            results[i] = gate(samples[i]);
        });

        int pass = 0, fail = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            var (ok, note) = results[i];
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {Path.GetFileName(samples[i])} ({Size(samples[i])}) {note}");
            if (ok) pass++; else fail++;
        }
        return (pass, fail);
    }

    public static void TryAdd(List<string> list, string path)
    {
        if (File.Exists(path)) list.Add(path);
    }

    public static IEnumerable<string> EnumerateSafe(string root, string pattern)
    {
        if (!Directory.Exists(root)) yield break;
        IEnumerable<string> seq;
        try { seq = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var f in seq) yield return f;
    }

    public static string MakeTmp(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* leave it */ }
    }

    public static bool FilesEqual(string a, string b)
    {
        var fa = File.ReadAllBytes(a);
        var fb = File.ReadAllBytes(b);
        if (fa.Length != fb.Length) return false;
        for (int i = 0; i < fa.Length; i++) if (fa[i] != fb[i]) return false;
        return true;
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    public static string ShortHash(string s) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..8];

    public static string Size(string path)
    {
        long n = new FileInfo(path).Length;
        if (n >= 1 << 20) return $"{n / (double)(1 << 20):F1} MB";
        if (n >= 1 << 10) return $"{n / (double)(1 << 10):F1} KB";
        return $"{n} B";
    }

    /// <summary>
    /// Recursively byte-compare two extracted trees. Returns
    /// (matched, differing, missingInB). Files present only in B are
    /// ignored (both tools should produce the same set).
    /// </summary>
    public static (int matched, int differing, int missingInB) ByteCompareTrees(string a, string b)
    {
        int matched = 0, differing = 0, missing = 0;
        foreach (var fa in Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories))
        {
            var rel = fa.Substring(a.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fb = Path.Combine(b, rel);
            if (!File.Exists(fb)) { missing++; continue; }
            if (FilesEqual(fa, fb)) matched++; else differing++;
        }
        return (matched, differing, missing);
    }

    /// <summary>
    /// Compare two trees by file-content multiset (SHA256), ignoring
    /// names/paths. Returns (sharedCount, onlyInA, onlyInB). Used where
    /// two tools extract identical bytes but name unresolved entries
    /// differently.
    /// </summary>
    public static (int shared, int onlyA, int onlyB) ContentSetCompare(string a, string b)
    {
        static Dictionary<string, int> Hashes(string root)
        {
            var d = new Dictionary<string, int>();
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var h = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
                d[h] = d.TryGetValue(h, out var c) ? c + 1 : 1;
            }
            return d;
        }
        var ha = Hashes(a);
        var hb = Hashes(b);
        int shared = 0, onlyA = 0, onlyB = 0;
        foreach (var (h, ca) in ha)
        {
            int cb = hb.TryGetValue(h, out var v) ? v : 0;
            shared += Math.Min(ca, cb);
            if (ca > cb) onlyA += ca - cb;
        }
        foreach (var (h, cb) in hb)
        {
            int ca = ha.TryGetValue(h, out var v) ? v : 0;
            if (cb > ca) onlyB += cb - ca;
        }
        return (shared, onlyA, onlyB);
    }
}
