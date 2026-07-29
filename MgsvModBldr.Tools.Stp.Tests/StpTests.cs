// Stp tool regression gate
using System.Diagnostics;
using MgsvModBldr.Tools.Stp;
using MgsvModBldr.Tools.Sbp;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Stp.Tests;

public sealed class StpTests : IToolTests
{
    public string Name => "stp";

    private static readonly string[] SbpDirs =
    {
        @"Z:\tpp\release\sound\asset\#Win",
        @"C:\Users\Blue\Downloads\test\tmp",
    };
    private const int MaxSbpToScan = 40; // unpack the smallest few sbp to mine .stp/.sab
    private const int MaxSamples = 6;

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- Stp (unpack + repack byte-match StpTool, .stp/.sab) ---");
        var samples = DiscoverSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; run `test stp --harvest` with Z:\\ / tmp attached)");
            return (0, 0);
        }
        return RunParallel(samples, Gate);
    }

    private static (bool ok, string note) Gate(string sample)
    {
        var work = MakeTmp("stp_rt_");
        try
        {
            var bucket = Path.GetDirectoryName(sample)!;
            var name = Path.GetFileName(sample);
            var refUnpack = Path.Combine(bucket, name + ".refunpack");
            var refRepack = Path.Combine(bucket, name + ".ref.repack");
            if (!Directory.Exists(refUnpack) || !File.Exists(refRepack))
                return (false, "no cached reference (re-run --harvest)");

            var staged = Path.Combine(work, name);
            File.Copy(sample, staged, overwrite: true);

            // (A) our unpack vs StpTool's unpacked tree
            var ourDir = StpPacker.Unpack(staged);
            var (matched, differing, missing) = ByteCompareTrees(refUnpack, ourDir);
            int refCount = Directory.GetFiles(refUnpack).Length;
            int ourCount = Directory.GetFiles(ourDir).Length;
            if (differing > 0 || missing > 0 || ourCount != refCount)
                return (false, $"unpack differs (matched={matched} differing={differing} missing={missing} ref={refCount} our={ourCount})");

            // (B) our repack vs StpTool's repack
            var repacked = StpPacker.Pack(ourDir, DetectVersion(staged));
            if (!FilesEqual(repacked, refRepack))
                return (false, $"repack differs from StpTool ({new FileInfo(repacked).Length} vs {new FileInfo(refRepack).Length} B)");

            var kind = name.EndsWith(".sab", StringComparison.OrdinalIgnoreCase) ? "sab" : "stp";
            return (true, $"{kind} {ourCount} file(s): unpack + repack match StpTool");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally { TryDelete(work); }
    }

    private static StpVersion DetectVersion(string stp)
    {
        // .sab has no version; .stp version byte is at offset 6.
        if (stp.EndsWith(".sab", StringComparison.OrdinalIgnoreCase)) return StpVersion.TPP;
        try
        {
            using var fs = File.OpenRead(stp);
            Span<byte> h = stackalloc byte[8];
            int n = 0; while (n < 8) { int r = fs.Read(h.Slice(n)); if (r == 0) break; n += r; }
            return h[6] == 0 ? StpVersion.GZ : StpVersion.TPP;
        }
        catch { return StpVersion.TPP; }
    }

    private static List<string> DiscoverSamples()
    {
        var dir = Path.Combine(FixturesDir, "stp");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".stp", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".sab", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => new FileInfo(f).Length)
                        .ToList();
    }

    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "stp");
        Directory.CreateDirectory(dst);

        var refExe = StpRefExe();
        if (refExe is null)
        {
            Console.WriteLine("  Stp: stpref oracle not built (set STPREF or build C:\\rsearch\\stpref) — skipping harvest");
            return;
        }

        // Mine .stp/.sab out of the smallest sbp packages, de-dup by content,
        // keep the smallest spread so the gate is fast and fixtures are lean.
        var sbps = SbpDirs.Where(Directory.Exists)
            .SelectMany(d => EnumerateSafe(d, "*.sbp"))
            .GroupBy(f => Path.GetFileName(f) + "_" + new FileInfo(f).Length)
            .Select(g => g.First())
            .OrderBy(f => new FileInfo(f).Length)
            .Take(MaxSbpToScan)
            .ToList();
        if (sbps.Count == 0) { Console.WriteLine("  Stp: no .sbp to mine (attach Z:\\ / tmp)"); return; }

        var mined = new List<string>();
        var mineDir = MakeTmp("stp_mine_");
        try
        {
            foreach (var sbp in sbps)
            {
                try
                {
                    var staged = Path.Combine(mineDir, Path.GetFileName(sbp));
                    File.Copy(sbp, staged, overwrite: true);
                    SbpPacker.Unpack(staged);
                    var sub = Path.Combine(mineDir, Path.GetFileNameWithoutExtension(sbp) + "_sbp");
                    if (Directory.Exists(sub))
                        mined.AddRange(Directory.EnumerateFiles(sub)
                            .Where(f => f.EndsWith(".stp", StringComparison.OrdinalIgnoreCase) ||
                                        f.EndsWith(".sab", StringComparison.OrdinalIgnoreCase)));
                }
                catch { /* skip bad sbp */ }
                if (mined.Count >= MaxSamples * 3) break;
            }

            var picks = mined.GroupBy(f => Path.GetFileName(f) + "_" + new FileInfo(f).Length)
                             .Select(g => g.First())
                             .OrderBy(f => new FileInfo(f).Length)
                             .Take(MaxSamples)
                             .ToList();
            if (picks.Count == 0) { Console.WriteLine("  Stp: no .stp/.sab inside the scanned sbp"); return; }

            int copied = 0;
            foreach (var src in picks)
            {
                try
                {
                    var bucket = Path.Combine(dst, ShortHash(src + new FileInfo(src).Length));
                    Directory.CreateDirectory(bucket);
                    var local = Path.Combine(bucket, Path.GetFileName(src));
                    File.Copy(src, local, overwrite: true);
                    if (GenerateReference(local, refExe)) copied++;
                }
                catch (Exception ex) { Console.WriteLine($"  Stp: skip {Path.GetFileName(src)} ({ex.Message})"); }
            }
            Console.WriteLine($"  Stp: harvested {copied} sample(s) to {dst}");
        }
        finally { TryDelete(mineDir); }
    }

    private static bool GenerateReference(string sample, string refExe)
    {
        var bucket = Path.GetDirectoryName(sample)!;
        var name = Path.GetFileName(sample);
        var ext = name.EndsWith(".sab", StringComparison.OrdinalIgnoreCase) ? "sab" : "stp";
        var tmp = MakeTmp("stp_ref_");
        try
        {
            var tmpFile = Path.Combine(tmp, name);
            File.Copy(sample, tmpFile, overwrite: true);

            bool gz = DetectVersion(tmpFile) == StpVersion.GZ;
            RunRef(refExe, tmpFile, gz);                              // -> tmp/<stem>_<ext>/
            var refDir = Path.Combine(tmp, Path.GetFileNameWithoutExtension(name) + "_" + ext);
            if (!Directory.Exists(refDir)) return false;

            var cacheDir = Path.Combine(bucket, name + ".refunpack");
            CopyTree(refDir, cacheDir);

            RunRef(refExe, refDir, gz);                              // -> tmp/<stem>.<ext> (the repack)
            var refRepack = Path.Combine(tmp, name);
            if (!File.Exists(refRepack)) return false;
            File.Copy(refRepack, Path.Combine(bucket, name + ".ref.repack"), overwrite: true);
            return true;
        }
        finally { TryDelete(tmp); }
    }

    private static void RunRef(string refExe, string arg, bool gz)
    {
        var args = gz ? $"-gz \"{arg}\"" : $"\"{arg}\"";
        var psi = new ProcessStartInfo(refExe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        // StpTool logs per-entry to stdout — drain both pipes or it can block.
        _ = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit(120000);
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
    }

    private static string StpRefExe()
    {
        var env = Environment.GetEnvironmentVariable("STPREF");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var def = @"C:\rsearch\stpref\bin\Release\net10.0\stpref.exe";
        return File.Exists(def) ? def : null;
    }
}
