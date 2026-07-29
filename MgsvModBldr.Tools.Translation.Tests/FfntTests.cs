// Ffnt tool regression gate
using System.Diagnostics;
using MgsvModBldr.Tools.Translation;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Translation.Tests;

public sealed class FfntTests : IToolTests
{
    public string Name => "ffnt";

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- Ffnt (round-trip vs game file + xml vs FfntTool) ---");
        var samples = DiscoverSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, TryRoundtrip);
    }

    private static (bool ok, string note) TryRoundtrip(string ffnt)
    {
        var work = MakeTmp("ffnt_rt_");
        try
        {
            var srcDir = Path.GetDirectoryName(ffnt) ?? ".";
            var staged = Path.Combine(work, Path.GetFileName(ffnt));
            File.Copy(ffnt, staged, overwrite: true);

            // Decompile: <staged>.ffnt -> <staged>.ffnt.xml + <stem>_N.png
            var myXml = FfntConverter.Unpack(staged);

            // (B) my XML must byte-match the FfntTool reference XML.
            var refXml = Path.Combine(srcDir, Path.GetFileName(ffnt) + ".ref.xml");
            string noteB;
            if (File.Exists(refXml))
            {
                if (!FilesEqual(myXml, refXml))
                    return (false, "xml differs from FfntTool reference");
                noteB = "xml matches FfntTool";
            }
            else noteB = "no ref xml";

            // (A) recompile (reads the layer PNGs we just wrote) must
            // byte-match the ORIGINAL game .ffnt.
            var repacked = FfntConverter.Pack(myXml); // overwrites staged
            if (!FilesEqual(repacked, ffnt))
                return (false, "repack differs from original .ffnt");

            return (true, $"round-trip byte-exact; {noteB}");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally { TryDelete(work); }
    }

    private static List<string> DiscoverSamples()
    {
        var dir = Path.Combine(FixturesDir, "ffnt");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.ffnt", SearchOption.AllDirectories)
                        .OrderBy(f => new FileInfo(f).Length)
                        .ToList();
    }

    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "ffnt");
        Directory.CreateDirectory(dst);
        try
        {
            var pool = EnumerateSafe(@"Z:\tpp\release\font", "*.ffnt").ToList();
            if (pool.Count == 0) { Console.WriteLine("  Ffnt: no .ffnt under Z:\\tpp\\release\\font"); return; }

            int copied = 0;
            foreach (var src in pool)
            {
                var bucket = Path.Combine(dst, ShortHash(src));
                Directory.CreateDirectory(bucket);
                var local = Path.Combine(bucket, Path.GetFileName(src));
                File.Copy(src, local, overwrite: true);
                GenerateReference(local);
                copied++;
            }
            Console.WriteLine($"  Ffnt: harvested {copied} sample(s) to {dst}");
        }
        catch (Exception ex) { Console.WriteLine($"  Ffnt harvest failed: {ex.Message}"); }
    }

    private static void GenerateReference(string stagedFfnt)
    {
        var refDll = Environment.GetEnvironmentVariable("FFNTREF");
        if (string.IsNullOrWhiteSpace(refDll) || !File.Exists(refDll))
            refDll = @"C:\rsearch\ffntref\bin\Release\net10.0-windows\FfntTool.dll";
        if (!File.Exists(refDll)) return;

        var dir  = Path.GetDirectoryName(stagedFfnt)!;
        var stem = Path.GetFileNameWithoutExtension(stagedFfnt);
        try
        {
            var psi = new ProcessStartInfo("dotnet", $"\"{refDll}\" \"{stagedFfnt}\"")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using (var proc = Process.Start(psi)) { proc?.WaitForExit(30000); }
            var producedDir = Path.Combine(dir, stem);
            var producedXml = Path.Combine(producedDir, stem + ".xml");
            if (File.Exists(producedXml))
                File.Move(producedXml, Path.Combine(dir, Path.GetFileName(stagedFfnt) + ".ref.xml"), overwrite: true);
            if (Directory.Exists(producedDir)) Directory.Delete(producedDir, recursive: true);
        }
        catch { /* reference is best-effort */ }
    }
}
