// Mtar tool regression gate
using System.Diagnostics;
using MgsvModBldr.Tools.Mtar;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Mtar.Tests;

public sealed class MtarTests : IToolTests
{
    public string Name => "mtar";
    private const string DefaultSamplesDir = @"C:\Users\Blue\Downloads\test\tmp";
    private const int MaxSamples = 20;

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- Mtar (xml + repack byte-match MtarTool, v1+v2) ---");
        var samples = DiscoverSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; set MTAR_SAMPLES_DIR and run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, TryRoundtrip);
    }

    private static (bool ok, string note) TryRoundtrip(string mtar)
    {
        var work = MakeTmp("mtar_rt_");
        try
        {
            var srcDir = Path.GetDirectoryName(mtar) ?? ".";
            var staged = Path.Combine(work, Path.GetFileName(mtar));
            File.Copy(mtar, staged, overwrite: true);

            int ver = MtarConverter.GetMtarType(staged);
            var myXml = MtarConverter.Unpack(staged); // -> <staged>.mtar.xml + <stem>_mtar/

            var refXml = Path.Combine(srcDir, Path.GetFileName(mtar) + ".ref.xml");
            string noteA = File.Exists(refXml)
                ? (FilesEqual(myXml, refXml) ? "xml matches MtarTool" : "xml differs from MtarTool")
                : "no ref xml";

            var repacked = MtarConverter.Pack(myXml); // overwrites staged, reads <stem>_mtar/

            // GROUND TRUTH: a repack must reproduce the game's own file byte for byte. Comparing
            // against MtarTool's output instead let a packer bug ship — MtarTool is not itself
            // byte-exact, so its "reference" repack disagrees with Konami. Only fall back to it
            // when we cannot match the original.
            if (FilesEqual(repacked, mtar))
                return (true, $"v{ver}: repack BYTE-EXACT vs original; {noteA}");

            var refRepack = Path.Combine(srcDir, Path.GetFileName(mtar) + ".ref.repack");
            if (File.Exists(refRepack) && FilesEqual(repacked, refRepack))
                return (true, $"v{ver}: repack matches MtarTool (not byte-exact vs original); {noteA}");

            return (false, $"v{ver}: repack reproduces neither the original nor MtarTool");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally { TryDelete(work); }
    }

    private static List<string> DiscoverSamples()
    {
        var dir = Path.Combine(FixturesDir, "mtar");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.mtar", SearchOption.AllDirectories)
                        .OrderBy(f => new FileInfo(f).Length)
                        .ToList();
    }

    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "mtar");
        Directory.CreateDirectory(dst);

        var dir = Environment.GetEnvironmentVariable("MTAR_SAMPLES_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) dir = DefaultSamplesDir;
        if (!Directory.Exists(dir)) { Console.WriteLine("  Mtar: no MTAR_SAMPLES_DIR (.mtar not loose on Z:\\)"); return; }

        try
        {
            var rng = new Random();
            // De-dup by name+size, then bucket by version so both v1 and v2 are covered.
            var uniq = EnumerateSafe(dir, "*.mtar")
                .GroupBy(f => Path.GetFileName(f) + "_" + new FileInfo(f).Length)
                .Select(g => g.First())
                .ToList();

            var byVer = uniq.OrderBy(_ => rng.Next())
                            .GroupBy(SafeVersion)
                            .ToDictionary(g => g.Key, g => g.ToList());

            var picks = new List<string>();
            foreach (var ver in new[] { 1, 2 })
                if (byVer.TryGetValue(ver, out var list))
                    picks.AddRange(list.Take(MaxSamples / 2));
            if (picks.Count == 0) picks = uniq.Take(MaxSamples).ToList();

            int copied = 0;
            foreach (var src in picks)
            {
                var bucket = Path.Combine(dst, ShortHash(src));
                Directory.CreateDirectory(bucket);
                var local = Path.Combine(bucket, Path.GetFileName(src));
                File.Copy(src, local, overwrite: true);
                GenerateReference(local);
                copied++;
            }
            Console.WriteLine($"  Mtar: harvested {copied} sample(s) to {dst}");
        }
        catch (Exception ex) { Console.WriteLine($"  Mtar harvest failed: {ex.Message}"); }
    }

    private static int SafeVersion(string path)
    {
        try { return MtarConverter.GetMtarType(path); } catch { return 0; }
    }

    private static void GenerateReference(string stagedMtar)
    {
        var refDll = Environment.GetEnvironmentVariable("MTARREF");
        if (string.IsNullOrWhiteSpace(refDll) || !File.Exists(refDll))
            refDll = @"C:\rsearch\mtarref\bin\Release\net10.0\MtarTool.dll";
        if (!File.Exists(refDll)) return;

        var bucket = Path.GetDirectoryName(stagedMtar)!;
        var name = Path.GetFileName(stagedMtar);
        var tmp = MakeTmp("mtar_ref_");
        try
        {
            var tmpMtar = Path.Combine(tmp, name);
            File.Copy(stagedMtar, tmpMtar, overwrite: true);
            RunRef(refDll, tmpMtar);                 // -> tmp/<name>.mtar.xml + tmp/<stem>_mtar/
            var producedXml = tmpMtar + ".xml";
            if (!File.Exists(producedXml)) return;
            File.Copy(producedXml, Path.Combine(bucket, name + ".ref.xml"), overwrite: true);

            RunRef(refDll, producedXml);             // -> tmp/<name>.mtar (overwrites tmpMtar)
            if (File.Exists(tmpMtar))
                File.Copy(tmpMtar, Path.Combine(bucket, name + ".ref.repack"), overwrite: true);
        }
        finally { TryDelete(tmp); }
    }

    private static void RunRef(string refDll, string arg)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{refDll}\" \"{arg}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(refDll), // dict + hashed_names.txt live by the dll
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        // MtarTool spams stdout (per-entry NameResolver logging); drain both
        // pipes concurrently or the child can block on a full buffer.
        _ = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit(60000);
    }
}
