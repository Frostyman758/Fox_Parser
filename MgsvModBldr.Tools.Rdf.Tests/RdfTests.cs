using System.Diagnostics;
using MgsvModBldr.Tools.Rdf;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Rdf.Tests;

/// <summary>
/// Rdf (.rdf radio dialogue, GZ v1 + TPP v3) gate — byte-exact parity
/// with Atvaark's RdfTool: (A) my XML byte-matches RdfTool's XML, and
/// (B) my repack byte-matches RdfTool's repack. RdfTool rebuilds the
/// binary from the parsed structure (re-derives the dialogueEvent/chara
/// index tables, offset tables), so it can be lossy vs the game file ->
/// gate is reference-parity, like spch/subp. .rdf live in FPKs; samples
/// come from the mod-builder tmp (RDF_SAMPLES_DIR). Oracle: RdfToolRef
/// (RDFREF).
/// </summary>
public sealed class RdfTests : IToolTests
{
    public string Name => "rdf";
    private const string DefaultSamplesDir = @"C:\Users\Blue\Downloads\test\tmp";
    private const int MaxSamples = 16;

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- Rdf (xml + repack byte-match RdfTool, v1+v3) ---");
        var samples = DiscoverSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; set RDF_SAMPLES_DIR and run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, TryRoundtrip);
    }

    private static (bool ok, string note) TryRoundtrip(string rdf)
    {
        var work = MakeTmp("rdf_rt_");
        try
        {
            var srcDir = Path.GetDirectoryName(rdf) ?? ".";
            var staged = Path.Combine(work, Path.GetFileName(rdf));
            File.Copy(rdf, staged, overwrite: true);

            int ver = PeekVersion(staged);
            var myXml = SafeUnpack(staged);
            if (myXml == null) return (false, $"v{ver}: unpack threw");

            var refXml = Path.Combine(srcDir, Path.GetFileName(rdf) + ".ref.xml");
            string noteA;
            if (File.Exists(refXml))
            {
                if (!FilesEqual(myXml, refXml)) return (false, $"v{ver}: xml differs from RdfTool reference");
                noteA = "xml matches RdfTool";
            }
            else noteA = "no ref xml";

            var repacked = RdfConverter.Pack(myXml);
            var refRepack = Path.Combine(srcDir, Path.GetFileName(rdf) + ".ref.repack");
            string noteB;
            if (File.Exists(refRepack))
            {
                if (!FilesEqual(repacked, refRepack)) return (false, $"v{ver}: repack differs from RdfTool reference");
                noteB = "repack matches RdfTool";
            }
            else noteB = "no ref repack";

            return (true, $"v{ver}: {noteA}; {noteB}");
        }
        catch (Exception ex)
        {
            return (false, $"exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally { TryDelete(work); }
    }

    private static string SafeUnpack(string rdf)
    {
        try { return RdfConverter.Unpack(rdf); } catch { return null; }
    }

    private static int PeekVersion(string rdf)
    {
        try { using var r = new BinaryReader(File.OpenRead(rdf)); return r.ReadByte(); }
        catch { return -1; }
    }

    private static List<string> DiscoverSamples()
    {
        var dir = Path.Combine(FixturesDir, "rdf");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.rdf", SearchOption.AllDirectories)
                        .OrderBy(f => new FileInfo(f).Length)
                        .ToList();
    }

    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "rdf");
        Directory.CreateDirectory(dst);

        var dir = Environment.GetEnvironmentVariable("RDF_SAMPLES_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) dir = DefaultSamplesDir;
        if (!Directory.Exists(dir)) { Console.WriteLine("  Rdf: no RDF_SAMPLES_DIR (.rdf not loose on Z:\\)"); return; }

        try
        {
            var rng = new Random();
            var uniq = EnumerateSafe(dir, "*.rdf")
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
            Console.WriteLine($"  Rdf: harvested {copied} sample(s) to {dst}");
        }
        catch (Exception ex) { Console.WriteLine($"  Rdf harvest failed: {ex.Message}"); }
    }

    private static void GenerateReference(string stagedRdf)
    {
        var refDll = Environment.GetEnvironmentVariable("RDFREF");
        if (string.IsNullOrWhiteSpace(refDll) || !File.Exists(refDll))
            refDll = @"C:\rsearch\rdfref\bin\Release\net10.0\RdfTool.dll";
        if (!File.Exists(refDll)) return;

        var bucket = Path.GetDirectoryName(stagedRdf)!;
        var name = Path.GetFileName(stagedRdf);
        var stem = Path.GetFileNameWithoutExtension(stagedRdf);
        var tmp = MakeTmp("rdf_ref_");
        try
        {
            // RdfTool appends discovered strings to rdf_user_dictionary.txt
            // in its own dir during pack, which would pollute later unpack
            // resolution. Restore the canonical (shipped) user dict before
            // each run so the oracle matches our tool deterministically.
            var refDir = Path.GetDirectoryName(refDll)!;
            var canonicalUserDict = Path.Combine(AppContext.BaseDirectory, "rdf_user_dictionary.txt");
            if (File.Exists(canonicalUserDict))
                File.Copy(canonicalUserDict, Path.Combine(refDir, "rdf_user_dictionary.txt"), overwrite: true);

            RunRef(refDll, stagedRdf, tmp);
            var producedXml = Path.Combine(tmp, stem + ".rdf.xml");
            if (!File.Exists(producedXml)) return;
            File.Copy(producedXml, Path.Combine(bucket, name + ".ref.xml"), overwrite: true);

            RunRef(refDll, producedXml, tmp);
            var producedRdf = Path.Combine(tmp, stem + ".rdf");
            if (File.Exists(producedRdf))
                File.Copy(producedRdf, Path.Combine(bucket, name + ".ref.repack"), overwrite: true);
        }
        finally { TryDelete(tmp); }
    }

    private static void RunRef(string refDll, string arg, string cwd)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{refDll}\" \"{arg}\"")
        {
            WorkingDirectory = cwd, // RdfTool writes outputs relative to CWD; dicts load from the dll dir
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        _ = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit(60000);
    }
}
