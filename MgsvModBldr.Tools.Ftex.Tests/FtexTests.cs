// Ftex tool regression gate
using System.Diagnostics;
using MgsvModBldr.Tools.Ftex;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Ftex.Tests;

public sealed class FtexTests : IToolTests
{
    public string Name => "ftex";
    private const int MaxFtexSamples = 8;

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- Ftex (dds vs FtexTool reference + round-trip) ---");
        var samples = DiscoverSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, TryRoundtrip);
    }

    private static (bool ok, string note) TryRoundtrip(string ftex)
    {
        var work = MakeTmp("ftex_rt_");
        try
        {
            // Stage: copy the .ftex + every sibling .ftexs into work.
            var srcDir = Path.GetDirectoryName(ftex) ?? ".";
            var stem   = Path.GetFileNameWithoutExtension(ftex);
            foreach (var sibling in Directory.EnumerateFiles(srcDir, stem + ".*"))
                File.Copy(sibling, Path.Combine(work, Path.GetFileName(sibling)), overwrite: true);

            var staged = Path.Combine(work, Path.GetFileName(ftex));
            var dds1   = FtexPacker.Unpack(staged);

            // (A) ftex -> dds must byte-match the FtexTool reference DDS
            // cached next to the fixture (<stem>.ref.dds).
            var refDds = Path.Combine(srcDir, stem + ".ref.dds");
            string note;
            if (File.Exists(refDds))
            {
                if (!FilesEqual(dds1, refDds))
                    return (false, "dds differs from FtexTool reference");
                note = "dds byte-matches FtexTool";
            }
            else note = "no FtexTool ref (round-trip only)";

            // (B) round-trip stability: dds -> ftex -> dds reproduces dds1.
            var ftex2 = FtexPacker.Pack(dds1);
            var dds2  = FtexPacker.Unpack(ftex2);
            if (!FilesEqual(dds1, dds2))
                return (false, "dds not stable across round-trip");

            return (true, note + "; round-trip ok");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally { TryDelete(work); }
    }

    private static List<string> DiscoverSamples()
    {
        var dir = Path.Combine(FixturesDir, "ftex");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.ftex", SearchOption.AllDirectories)
                        .OrderBy(f => new FileInfo(f).Length)
                        .ToList();
    }

    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "ftex");
        Directory.CreateDirectory(dst);

        try
        {
            var pool = EnumerateSafe(@"Z:\tpp\release", "*.ftex").Take(2000).ToList();
            if (pool.Count == 0)
            {
                Console.WriteLine("  Ftex: no .ftex files found under Z:\\tpp\\release");
                return;
            }

            var rng = new Random();
            var byShape = pool.OrderBy(_ => rng.Next())
                              .GroupBy(f => CountSiblings(f))
                              .OrderBy(g => g.Key);

            int copied = 0;
            int perBucket = Math.Max(1, MaxFtexSamples / 3);
            foreach (var group in byShape)
            {
                int taken = 0;
                foreach (var ftex in group)
                {
                    if (copied >= MaxFtexSamples) break;
                    if (taken >= perBucket) break;
                    var stem   = Path.GetFileNameWithoutExtension(ftex);
                    var srcDir = Path.GetDirectoryName(ftex) ?? ".";

                    var bucket = Path.Combine(dst, ShortHash(ftex));
                    Directory.CreateDirectory(bucket);
                    foreach (var sibling in Directory.EnumerateFiles(srcDir, stem + ".*"))
                        File.Copy(sibling, Path.Combine(bucket, Path.GetFileName(sibling)), overwrite: true);

                    GenerateReference(Path.Combine(bucket, stem + ".ftex"), stem);

                    copied++;
                    taken++;
                }
                if (copied >= MaxFtexSamples) break;
            }

            Console.WriteLine($"  Ftex: harvested {copied} sample(s) to {dst}");
        }
        catch (Exception ex) { Console.WriteLine($"  Ftex harvest failed: {ex.Message}"); }
    }

    private static int CountSiblings(string ftex)
    {
        var stem = Path.GetFileNameWithoutExtension(ftex);
        var dir  = Path.GetDirectoryName(ftex) ?? ".";
        try { return Directory.EnumerateFiles(dir, stem + ".*.ftexs").Count(); }
        catch { return 0; }
    }

    private static void GenerateReference(string stagedFtex, string stem)
    {
        var refDll = Environment.GetEnvironmentVariable("FTEXREF");
        if (string.IsNullOrWhiteSpace(refDll) || !File.Exists(refDll))
            refDll = @"C:\rsearch\ftexref\bin\Release\net8.0\FtexToolRef.dll";
        if (!File.Exists(refDll)) return;

        var dir = Path.GetDirectoryName(stagedFtex)!;
        try
        {
            var psi = new ProcessStartInfo("dotnet", $"\"{refDll}\" \"{stagedFtex}\"")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using (var proc = Process.Start(psi)) { proc?.WaitForExit(30000); }
            var produced = Path.Combine(dir, stem + ".dds");
            var refDds   = Path.Combine(dir, stem + ".ref.dds");
            if (File.Exists(produced)) File.Move(produced, refDds, overwrite: true);
        }
        catch { /* reference is best-effort; gate falls back to round-trip */ }
    }
}
