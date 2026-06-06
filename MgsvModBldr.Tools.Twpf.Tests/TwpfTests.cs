using System.Diagnostics;
using System.IO.Compression;
using MgsvModBldr.Tools.Twpf;
using MgsvModBldr.Tools.Testing;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Twpf.Tests;

/// <summary>
/// Twpf (.twpf weather-parameter) gate. The format is losslessly
/// invertible, so two byte-exact checks:
///   (A) round-trip vs the ORIGINAL game file — my unpack→repack must
///       byte-match the original .twpf (ground truth; always available).
///   (B) my XML byte-matches TwpfXmlTool's XML (cached
///       <c>&lt;name&gt;.twpf.ref.xml</c>) — reference-tool parity.
/// Samples come from PC.zip (user-supplied; .twpf live inside FPKDs on
/// Z:\, not loose).
/// </summary>
public sealed class TwpfTests : IToolTests
{
    public string Name => "twpf";

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- Twpf (round-trip vs game file + xml vs TwpfXmlTool) ---");
        var samples = DiscoverSamples();
        if (samples.Count == 0)
        {
            Console.WriteLine("  (no fixtures; run with --harvest)");
            return (0, 0);
        }
        return RunParallel(samples, TryRoundtrip);
    }

    private static (bool ok, string note) TryRoundtrip(string twpf)
    {
        var work = MakeTmp("twpf_rt_");
        try
        {
            var srcDir = Path.GetDirectoryName(twpf) ?? ".";
            var staged = Path.Combine(work, Path.GetFileName(twpf));
            File.Copy(twpf, staged, overwrite: true);

            // Decompile: <staged>.twpf -> <staged>.twpf.xml
            var myXml = TwpfConverter.Unpack(staged);

            // (B) my XML must byte-match the TwpfXmlTool reference XML.
            var refXml = Path.Combine(srcDir, Path.GetFileName(twpf) + ".ref.xml");
            string noteB;
            if (File.Exists(refXml))
            {
                if (!FilesEqual(myXml, refXml))
                    return (false, "xml differs from TwpfXmlTool reference");
                noteB = "xml matches TwpfXmlTool";
            }
            else noteB = "no ref xml";

            // (A) recompile must byte-match the ORIGINAL game .twpf.
            var repacked = TwpfConverter.Pack(myXml); // overwrites staged
            if (!FilesEqual(repacked, twpf))
                return (false, "repack differs from original .twpf");

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
        var dir = Path.Combine(FixturesDir, "twpf");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.twpf", SearchOption.AllDirectories)
                        .OrderBy(f => new FileInfo(f).Length)
                        .ToList();
    }

    /// <summary>
    /// Harvest .twpf from the user-supplied PC.zip (env TWPF_SAMPLES_ZIP
    /// or the default Downloads path) into hash-keyed buckets, caching the
    /// TwpfXmlTool reference XML for each.
    /// </summary>
    public void Harvest()
    {
        var dst = Path.Combine(FixturesDir, "twpf");
        Directory.CreateDirectory(dst);

        var zipPath = Environment.GetEnvironmentVariable("TWPF_SAMPLES_ZIP");
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            zipPath = @"C:\Users\Blue\Downloads\PC.zip";
        if (!File.Exists(zipPath))
        {
            Console.WriteLine($"  Twpf: sample zip not found ({zipPath})");
            return;
        }

        try
        {
            int copied = 0;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    if (!entry.FullName.EndsWith(".twpf", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileName(entry.FullName);
                    if (string.IsNullOrEmpty(name)) continue;
                    var bucket = Path.Combine(dst, ShortHash(entry.FullName));
                    Directory.CreateDirectory(bucket);
                    var local = Path.Combine(bucket, name);
                    entry.ExtractToFile(local, overwrite: true);
                    GenerateReference(local);
                    copied++;
                }
            }
            Console.WriteLine($"  Twpf: harvested {copied} sample(s) from {Path.GetFileName(zipPath)} to {dst}");
        }
        catch (Exception ex) { Console.WriteLine($"  Twpf harvest failed: {ex.Message}"); }
    }

    /// <summary>
    /// Run TwpfXmlToolRef (built from Atvaark's source) on a staged .twpf
    /// to cache <c>&lt;name&gt;.twpf.ref.xml</c>. The reference writes
    /// <c>&lt;stem&gt;.twpf.xml</c> relative to its working directory, so we
    /// run it with the bucket as CWD and rename. Locate via TWPFREF env or
    /// the default build path; best-effort (gate (A) still holds without it).
    /// </summary>
    private static void GenerateReference(string stagedTwpf)
    {
        var refDll = Environment.GetEnvironmentVariable("TWPFREF");
        if (string.IsNullOrWhiteSpace(refDll) || !File.Exists(refDll))
            refDll = @"C:\rsearch\twpfref\bin\Release\net10.0\TwpfXmlTool.dll";
        if (!File.Exists(refDll)) return;

        var dir  = Path.GetDirectoryName(stagedTwpf)!;
        var stem = Path.GetFileNameWithoutExtension(stagedTwpf);
        try
        {
            var psi = new ProcessStartInfo("dotnet", $"\"{refDll}\" \"{stagedTwpf}\"")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using (var proc = Process.Start(psi)) { proc?.WaitForExit(30000); }
            var produced = Path.Combine(dir, stem + ".twpf.xml");
            var refXml   = Path.Combine(dir, Path.GetFileName(stagedTwpf) + ".ref.xml");
            if (File.Exists(produced)) File.Move(produced, refXml, overwrite: true);
        }
        catch { /* reference is best-effort */ }
    }
}
