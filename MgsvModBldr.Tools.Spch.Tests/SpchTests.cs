// Spch tool regression gate
using System.Diagnostics;
using MgsvModBldr.Tools.Spch;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Spch.Tests;

public sealed class SpchTests : IToolTests
{
    public string Name => "spch";
    private const string DefaultSamplesDir = @"C:\Users\Blue\Downloads\test\tmp";
    private const int MaxSamples = 12;

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- Spch (xml + repack byte-match SpchTool) ---");
        var samples = DiscoverSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; set SPCH_SAMPLES_DIR and run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, TryRoundtrip);
    }

    private static (bool ok, string note) TryRoundtrip(string spch)
    {
        var work = MakeTmp("spch_rt_");
        try
        {
            var srcDir = Path.GetDirectoryName(spch) ?? ".";
            var staged = Path.Combine(work, Path.GetFileName(spch));
            File.Copy(spch, staged, overwrite: true);

            var myXml = SpchConverter.Unpack(staged);

            var refXml = Path.Combine(srcDir, Path.GetFileName(spch) + ".ref.xml");
            string noteA;
            if (File.Exists(refXml))
            {
                if (!FilesEqual(myXml, refXml)) return (false, "xml differs from SpchTool reference");
                noteA = "xml matches SpchTool";
            }
            else noteA = "no ref xml";

            var repacked = SpchConverter.Pack(myXml);
            var refRepack = Path.Combine(srcDir, Path.GetFileName(spch) + ".ref.repack");
            string noteB;
            if (File.Exists(refRepack))
            {
                if (!FilesEqual(repacked, refRepack)) return (false, "repack differs from SpchTool reference");
                noteB = "repack matches SpchTool";
            }
            else noteB = "no ref repack";

            return (true, $"{noteA}; {noteB}");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally { TryDelete(work); }
    }

    private static List<string> DiscoverSamples()
    {
        var dir = Path.Combine(FixturesDir, "spch");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.spch", SearchOption.AllDirectories)
                        .OrderBy(f => new FileInfo(f).Length)
                        .ToList();
    }

    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "spch");
        Directory.CreateDirectory(dst);

        var dir = Environment.GetEnvironmentVariable("SPCH_SAMPLES_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) dir = DefaultSamplesDir;
        if (!Directory.Exists(dir)) { Console.WriteLine("  Spch: no SPCH_SAMPLES_DIR (.spch not loose on Z:\\)"); return; }

        try
        {
            var rng = new Random();
            var uniq = EnumerateSafe(dir, "*.spch")
                .GroupBy(f => Path.GetFileName(f) + "_" + new FileInfo(f).Length)
                .Select(g => g.First())
                .OrderBy(_ => rng.Next()).Take(MaxSamples).ToList();

            int copied = 0;
            foreach (var src in uniq)
            {
                var bucket = Path.Combine(dst, ShortHash(src));
                Directory.CreateDirectory(bucket);
                var local = Path.Combine(bucket, Path.GetFileName(src));
                File.Copy(src, local, overwrite: true);
                GenerateReference(local);
                copied++;
            }
            Console.WriteLine($"  Spch: harvested {copied} sample(s) to {dst}");
        }
        catch (Exception ex) { Console.WriteLine($"  Spch harvest failed: {ex.Message}"); }
    }

    private static void GenerateReference(string stagedSpch)
    {
        var refDll = Environment.GetEnvironmentVariable("SPCHREF");
        if (string.IsNullOrWhiteSpace(refDll) || !File.Exists(refDll))
            refDll = @"C:\rsearch\spchref\bin\Release\net10.0\SpchTool.dll";
        if (!File.Exists(refDll)) return;

        var bucket = Path.GetDirectoryName(stagedSpch)!;
        var name = Path.GetFileName(stagedSpch);
        var stem = Path.GetFileNameWithoutExtension(stagedSpch);
        var tmp = MakeTmp("spch_ref_");
        try
        {
            // SpchTool writes <stem>.spch.xml / <stem>.spch to the CWD; run it there.
            RunRef(refDll, stagedSpch, tmp);
            var producedXml = Path.Combine(tmp, stem + ".spch.xml");
            if (!File.Exists(producedXml)) return;
            File.Copy(producedXml, Path.Combine(bucket, name + ".ref.xml"), overwrite: true);

            RunRef(refDll, producedXml, tmp);
            var producedSpch = Path.Combine(tmp, stem + ".spch");
            if (File.Exists(producedSpch))
                File.Copy(producedSpch, Path.Combine(bucket, name + ".ref.repack"), overwrite: true);
        }
        finally { TryDelete(tmp); }
    }

    private static void RunRef(string refDll, string arg, string cwd)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{refDll}\" \"{arg}\"")
        {
            WorkingDirectory = cwd, // SpchTool writes outputs relative to CWD
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        // SpchTool spams stdout heavily; drain both pipes concurrently or
        // the child blocks on a full buffer (deadlock) on content-heavy files.
        _ = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit(30000);
    }
}
