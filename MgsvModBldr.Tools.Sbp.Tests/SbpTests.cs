// Sbp tool regression gate
using MgsvModBldr.Tools.Sbp;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Sbp.Tests;

public sealed class SbpTests : IToolTests
{
    public string Name => "sbp";

    private static readonly string[] SampleDirs =
    {
        @"Z:\tpp\release\sound\asset\#Win",
        @"C:\Users\Blue\Downloads\test\tmp",
    };
    private const int MaxSamples = 8;          // keep the gate quick
    private const long MaxSampleBytes = 80L << 20; // skip the huge 180 MB banks

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- Sbp (byte-exact round-trip vs game file) ---");
        var samples = DiscoverSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; run `test sbp --harvest` with Z:\\ / tmp attached)");
            return (0, 0);
        }
        return RunParallel(samples, TryRoundtrip);
    }

    private static (bool ok, string note) TryRoundtrip(string sbp)
    {
        var work = MakeTmp("sbp_rt_");
        try
        {
            var staged = Path.Combine(work, Path.GetFileName(sbp));
            File.Copy(sbp, staged, overwrite: true);

            var manifest = SbpPacker.Unpack(staged);          // -> staged.json + _sbp/
            int count = CountEntries(staged);
            var repacked = SbpPacker.Pack(manifest);          // -> staged (overwrites)

            if (!FilesEqual(repacked, sbp))
                return (false, $"{count} sub-file(s): repack differs from original");

            return (true, $"{count} sub-file(s): round-trip byte-exact");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally { TryDelete(work); }
    }

    private static int CountEntries(string sbp)
    {
        try
        {
            using var fs = File.OpenRead(sbp);
            Span<byte> h = stackalloc byte[8];
            int n = 0; while (n < 8) { int r = fs.Read(h.Slice(n)); if (r == 0) break; n += r; }
            return h[4];
        }
        catch { return -1; }
    }

    private static List<string> DiscoverSamples()
    {
        var dir = Path.Combine(FixturesDir, "sbp");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.sbp", SearchOption.AllDirectories)
                        .OrderBy(f => new FileInfo(f).Length)
                        .ToList();
    }

    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "sbp");
        Directory.CreateDirectory(dst);

        // De-dup by name+size, drop the huge banks, then pick a spread so the
        // gate covers both multi-sub-file packages (bnk+stp) and trivial
        // single-entry ones — smallest within each group keeps it fast.
        var uniq = SampleDirs
            .Where(Directory.Exists)
            .SelectMany(d => EnumerateSafe(d, "*.sbp"))
            .GroupBy(f => Path.GetFileName(f) + "_" + new FileInfo(f).Length)
            .Select(g => g.First())
            .Where(f => new FileInfo(f).Length <= MaxSampleBytes)
            .OrderBy(f => new FileInfo(f).Length)
            .ToList();

        if (uniq.Count == 0) { Console.WriteLine("  Sbp: no .sbp samples found (attach Z:\\ / tmp)"); return; }

        var multi  = uniq.Where(f => CountEntries(f) >= 2).Take(MaxSamples / 2).ToList();
        var single = uniq.Where(f => CountEntries(f) < 2).Take(MaxSamples - multi.Count).ToList();
        var picks  = multi.Concat(single).Take(MaxSamples).ToList();
        uniq = picks.Count > 0 ? picks : uniq.Take(MaxSamples).ToList();

        int copied = 0;
        foreach (var src in uniq)
        {
            try
            {
                var bucket = Path.Combine(dst, ShortHash(src));
                Directory.CreateDirectory(bucket);
                File.Copy(src, Path.Combine(bucket, Path.GetFileName(src)), overwrite: true);
                copied++;
            }
            catch (Exception ex) { Console.WriteLine($"  Sbp: skip {Path.GetFileName(src)} ({ex.Message})"); }
        }
        Console.WriteLine($"  Sbp: harvested {copied} sample(s) to {dst}");
    }
}
