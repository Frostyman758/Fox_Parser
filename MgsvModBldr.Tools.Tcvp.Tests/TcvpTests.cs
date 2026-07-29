// Tcvp tool regression gate
using MgsvModBldr.Tools.Tcvp;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Tcvp.Tests;

public sealed class TcvpTests : IToolTests
{
    public string Name => "tcvp";
    private const string DefaultSamplesDir = @"C:\Users\Blue\Downloads\test\tmp";
    private const int MaxSamples = 24;

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- Tcvp (byte-exact round-trip vs game file, GZ+TPP) ---");
        var samples = DiscoverSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; set TCVP_SAMPLES_DIR and run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, TryRoundtrip);
    }

    private static (bool ok, string note) TryRoundtrip(string tcvp)
    {
        var work = MakeTmp("tcvp_rt_");
        try
        {
            var staged = Path.Combine(work, Path.GetFileName(tcvp));
            File.Copy(tcvp, staged, overwrite: true);

            int ver = PeekVersion(staged);
            var myXml = TcvpConverter.Unpack(staged);
            var repacked = TcvpConverter.Pack(myXml); // overwrites staged

            if (!FilesEqual(repacked, tcvp))
                return (false, $"v{ver}: repack differs from original .tcvp");

            return (true, $"v{ver}: round-trip byte-exact");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally { TryDelete(work); }
    }

    private static int PeekVersion(string tcvp)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(tcvp));
            r.ReadBytes(4); // TCVP
            return r.ReadUInt16(); // 0=GZ, 1=TPP
        }
        catch { return -1; }
    }

    private static List<string> DiscoverSamples()
    {
        var dir = Path.Combine(FixturesDir, "tcvp");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.tcvp", SearchOption.AllDirectories)
                        .OrderBy(f => new FileInfo(f).Length)
                        .ToList();
    }

    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "tcvp");
        Directory.CreateDirectory(dst);

        var dir = Environment.GetEnvironmentVariable("TCVP_SAMPLES_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) dir = DefaultSamplesDir;
        if (!Directory.Exists(dir)) { Console.WriteLine("  Tcvp: no TCVP_SAMPLES_DIR (.tcvp not loose on Z:\\)"); return; }

        try
        {
            var rng = new Random();
            // De-dup by name+size, bucket by version so both GZ and TPP are covered.
            var uniq = EnumerateSafe(dir, "*.tcvp")
                .GroupBy(f => Path.GetFileName(f) + "_" + new FileInfo(f).Length)
                .Select(g => g.First())
                .ToList();

            var byVer = uniq.OrderBy(_ => rng.Next()).GroupBy(PeekVersion)
                            .ToDictionary(g => g.Key, g => g.ToList());

            var picks = new List<string>();
            foreach (var v in new[] { 0, 1 })
                if (byVer.TryGetValue(v, out var list))
                    picks.AddRange(list.Take(MaxSamples / 2));
            if (picks.Count == 0) picks = uniq.Take(MaxSamples).ToList();

            int copied = 0;
            foreach (var src in picks)
            {
                var bucket = Path.Combine(dst, ShortHash(src));
                Directory.CreateDirectory(bucket);
                File.Copy(src, Path.Combine(bucket, Path.GetFileName(src)), overwrite: true);
                copied++;
            }
            Console.WriteLine($"  Tcvp: harvested {copied} sample(s) to {dst}");
        }
        catch (Exception ex) { Console.WriteLine($"  Tcvp harvest failed: {ex.Message}"); }
    }
}
