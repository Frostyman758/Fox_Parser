// Fsop tool regression gate
using MgsvModBldr.Tools.Fsop;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Fsop.Tests;

public sealed class FsopTests : IToolTests
{
    public string Name => "fsop";

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- FSOP (byte-exact gate) ---");
        var samples = DiscoverSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, s =>
        {
            var ok = RoundtripExact(s);
            return (ok, "");
        });
    }

    private static bool RoundtripExact(string fsop)
    {
        var work = MakeTmp("fsop_rt_");
        try
        {
            var unpackDir = Path.Combine(work, "unpacked");
            var repacked  = Path.Combine(work, Path.GetFileName(fsop));
            FsopPacker.Unpack(fsop, unpackDir);
            FsopPacker.Pack(unpackDir, repacked);
            return Sha256(File.ReadAllBytes(fsop)) == Sha256(File.ReadAllBytes(repacked));
        }
        finally { TryDelete(work); }
    }

    private static List<string> DiscoverSamples()
    {
        var hits = new List<string>();
        // Real-file path: any .fsop directly accessible from Z:\.
        // FSOPs aren't usually packed inside FPKs so we can hit Z:\
        // straight — no fixture harvest needed for this tool.
        TryAdd(hits, @"Z:\shaders\dx11\FxShaders_dx11.fsop");
        // Plus anything previously harvested.
        var dir = Path.Combine(FixturesDir, "fsop");
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*.fsop", SearchOption.AllDirectories))
                hits.Add(f);
        return hits;
    }

    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "fsop");
        Directory.CreateDirectory(dst);
        try
        {
            int copied = 0;
            foreach (var f in EnumerateSafe(@"Z:\shaders", "*.fsop"))
            {
                if (copied >= 3) break;
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
                copied++;
            }
            Console.WriteLine($"  FSOP: copied {copied} sample(s) to {dst}");
        }
        catch (Exception ex) { Console.WriteLine($"  FSOP harvest failed: {ex.Message}"); }
    }
}
