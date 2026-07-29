// Pftxs tool regression gate
using System.Diagnostics;
using MgsvModBldr.Tools.Pftxs;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Pftxs.Tests;

public sealed class PftxsTests : IToolTests
{
    public string Name => "pftxs";

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- PFTXS (extraction vs reference + pack round-trip) ---");
        var dir = Path.Combine(FixturesDir, "pftxs");
        if (!Directory.Exists(dir)) { Console.WriteLine("  (no fixtures; run with --harvest)"); return (0, 0); }
        var samples = Directory.EnumerateFiles(dir, "*.pftxs").OrderBy(f => new FileInfo(f).Length).ToList();
        if (samples.Count == 0) { Console.WriteLine("  (no fixtures; run with --harvest)"); return (0, 0); }
        return RunParallel(samples, TryRoundtrip);
    }

    private static (bool ok, string note) TryRoundtrip(string archive)
    {
        var work = MakeTmp("pftxs_rt_");
        try
        {
            var staged = Path.Combine(work, Path.GetFileName(archive));
            File.Copy(archive, staged, overwrite: true);

            var manifestPath = PftxsPacker.Unpack(staged);
            var stem = Path.GetFileNameWithoutExtension(archive);
            var extractDir = Path.Combine(work, stem + "_pftxs");

            var refDir = Path.Combine(FixturesDir, "pftxs", Path.GetFileName(archive) + "_ref");
            string note;
            if (Directory.Exists(refDir))
            {
                // Compare by CONTENT, not path: the reference names
                // unresolved entries <baseHex>.ext which TRUNCATES the
                // hash and can collide (silently dropping data). Our
                // full-hash names never collide, so we're a superset.
                // Gate: we must contain everything the reference produced
                // (onlyRef == 0); extra blobs we kept are us being MORE
                // complete.
                var (same, onlyRef, onlyMine) = ContentSetCompare(refDir, extractDir);
                if (onlyRef > 0)
                    return (false, $"vs reference content: missing {onlyRef} reference produced (of {same + onlyRef})");
                note = onlyMine > 0
                    ? $"{same} content-match reference (+{onlyMine} entries reference dropped to name-collision)"
                    : $"{same} files content-match reference";
            }
            else note = "no reference (round-trip only)";

            var repacked = Path.Combine(work, "repacked.pftxs");
            PftxsPacker.Pack(manifestPath, repacked);
            var reManifest = PftxsPacker.Unpack(repacked);
            var reDir = Path.Combine(work, "repacked_pftxs");
            var (m2, d2, miss2) = ByteCompareTrees(extractDir, reDir);
            if (d2 > 0 || miss2 > 0)
                return (false, $"pack round-trip: {d2} differ, {miss2} missing (of {m2 + d2 + miss2})");

            return (true, $"{note}; round-trip {m2} ok");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally { TryDelete(work); }
    }

    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "pftxs");
        Directory.CreateDirectory(dst);
        var gz = FindReferenceTool();
        try
        {
            var rng = new Random();
            var picks = EnumerateSafe(@"Z:\tpp\release\pack", "*.pftxs")
                .Where(f => new FileInfo(f).Length is > 2000 and < (20L << 20))
                .OrderBy(_ => rng.Next()).Take(5).ToList();
            int n = 0;
            foreach (var src in picks)
            {
                var local = Path.Combine(dst, Path.GetFileName(src));
                File.Copy(src, local, overwrite: true);
                if (gz is not null)
                {
                    var stem = Path.GetFileNameWithoutExtension(src);
                    var gzOut = Path.Combine(dst, stem + "_pftxs");
                    if (Directory.Exists(gzOut)) Directory.Delete(gzOut, true);
                    var psi = new ProcessStartInfo(gz, $"\"{local}\"")
                    {
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        UseShellExecute = false, CreateNoWindow = true,
                    };
                    using (var proc = Process.Start(psi)) { proc?.WaitForExit(120000); }
                    var refDir = local + "_ref";
                    if (Directory.Exists(refDir)) Directory.Delete(refDir, true);
                    if (Directory.Exists(gzOut)) Directory.Move(gzOut, refDir);
                }
                n++;
            }
            Console.WriteLine($"  PFTXS: harvested {n} archive(s){(gz is null ? " (no reference)" : " + references")} to {dst}");
        }
        catch (Exception ex) { Console.WriteLine($"  PFTXS harvest failed: {ex.Message}"); }
    }

    private static string FindReferenceTool()
    {
        var env = Environment.GetEnvironmentVariable("GZSTOOL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var def = @"C:\rsearch\gzstool\GzsTool.exe";
        return File.Exists(def) ? def : null;
    }
}
