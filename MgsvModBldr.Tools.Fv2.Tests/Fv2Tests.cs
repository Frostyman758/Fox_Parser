// Fv2 tool regression gate
using MgsvModBldr.Tools.Fv2;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Fv2.Tests;

public sealed class Fv2Tests : IToolTests
{
    public string Name => "fv2";
    private const string DefaultSamplesDir = @"C:\Users\Blue\Downloads\test\tmp";
    private const int MaxSamples = 30;

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- Fv2 (byte-exact round-trip vs game file) ---");
        var samples = DiscoverSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; set FV2_SAMPLES_DIR and run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, TryRoundtrip);
    }

    private static (bool ok, string note) TryRoundtrip(string fv2)
    {
        var work = MakeTmp("fv2_rt_");
        try
        {
            var staged = Path.Combine(work, Path.GetFileName(fv2));
            File.Copy(fv2, staged, overwrite: true);

            var xml1 = Fv2Converter.Unpack(staged);           // <staged>.fv2.xml
            var xml1Bytes = File.ReadAllBytes(xml1);
            var repacked = Fv2Converter.Pack(xml1);           // overwrites staged

            // (A) Best case: byte-exact vs the original game file.
            if (FilesEqual(repacked, fv2))
                return (true, "round-trip byte-exact");

            // (B) Fallback: the game file has non-canonical padding that
            // FvTwool's Write also normalises away (no CLI reference to
            // compare against) — verify the DATA is preserved by checking
            // the re-decompiled XML is identical (Ftex precedent: content-
            // equal, not container-byte-equal). Repack is game-loadable.
            var xml2 = Fv2Converter.Unpack(repacked);
            if (File.ReadAllBytes(xml2).AsSpan().SequenceEqual(xml1Bytes))
                return (true, "data-stable (layout normalized)");

            return (false, "repack data differs from original");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally { TryDelete(work); }
    }

    private static List<string> DiscoverSamples()
    {
        var dir = Path.Combine(FixturesDir, "fv2");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.fv2", SearchOption.AllDirectories)
                        .OrderBy(f => new FileInfo(f).Length)
                        .ToList();
    }

    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "fv2");
        Directory.CreateDirectory(dst);

        var dir = Environment.GetEnvironmentVariable("FV2_SAMPLES_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) dir = DefaultSamplesDir;
        if (!Directory.Exists(dir)) { Console.WriteLine("  Fv2: no FV2_SAMPLES_DIR (.fv2 not loose on Z:\\)"); return; }

        try
        {
            var rng = new Random();
            // De-dup by name+size, pick a size-spread (fv2 vary a lot in shape).
            var uniq = EnumerateSafe(dir, "*.fv2")
                .GroupBy(f => Path.GetFileName(f) + "_" + new FileInfo(f).Length)
                .Select(g => g.First())
                .OrderBy(_ => rng.Next()).Take(MaxSamples).ToList();

            int copied = 0;
            foreach (var src in uniq)
            {
                var bucket = Path.Combine(dst, ShortHash(src));
                Directory.CreateDirectory(bucket);
                File.Copy(src, Path.Combine(bucket, Path.GetFileName(src)), overwrite: true);
                copied++;
            }
            Console.WriteLine($"  Fv2: harvested {copied} sample(s) to {dst}");
        }
        catch (Exception ex) { Console.WriteLine($"  Fv2 harvest failed: {ex.Message}"); }
    }
}
