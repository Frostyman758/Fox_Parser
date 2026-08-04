// MotionGraph regression gate — round trip, invariants, and model coverage
using MgsvModBldr.Tools.Testing;

namespace MgsvModBldr.Tools.MotionGraph.Tests;

/// <summary>
/// The XML still carries the original file as a base64 image, so a byte-exact round trip proves
/// nothing about how much of the format is understood. Coverage does: walk every structure the
/// model knows, mark the bytes it accounts for, and report what is still riding along unread.
/// It only goes up as fields are modelled, and reaching 100% is what lets the image go.
/// </summary>
public sealed class MogTests : IToolTests
{
    public string Name => "mog";
    private static readonly string[] SampleDirs =
    {
        @"C:\rsearch\gzanim_tmp\corpus\tpp",
        @"C:\rsearch\gzanim_tmp\corpus\gz",
    };

    public void Harvest() { }

    public (int pass, int fail) Run()
    {
        Console.WriteLine("--- MotionGraph (round trip, invariants, model coverage) ---");
        var files = new List<string>();
        foreach (var d in SampleDirs)
            if (Directory.Exists(d)) files.AddRange(Directory.GetFiles(d, "*.mog"));
        if (files.Count == 0)
        {
            Console.WriteLine("  (no mog corpus found)");
            return (0, 0);
        }

        int pass = 0, fail = 0;
        long covered = 0, total = 0;
        foreach (var f in files.OrderBy(x => x))
        {
            var raw = File.ReadAllBytes(f);
            string note;
            bool ok;
            try
            {
                // the real conversion path: .mog -> .mog.xml -> .mog
                var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mog.xml");
                MogXml.Write(MogFile.Read(raw), MogPathPool.Find(raw), tmp);
                var built = MogBuilder.Build(MogXml.Read(tmp));
                File.Delete(tmp);
                var res = MogValidate.Run(built);
                var cov = MogCoverage.Measure(raw);
                covered += cov.Covered;
                total += raw.Length;
                ok = built.AsSpan().SequenceEqual(raw) && res.Ok;
                note = $"round trip {(built.AsSpan().SequenceEqual(raw) ? "byte-exact" : "DIFFERS")}"
                     + $"; invariants {(res.Ok ? "ok" : res.Errors.Count + " failed")}"
                     + $"; model covers {cov.Percent:0.0}%";
            }
            catch (Exception ex) { ok = false; note = $"{ex.GetType().Name}: {ex.Message}"; }

            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {Path.GetFileName(f)}  {note}");
            if (ok) pass++; else fail++;
        }
        var agg = new Dictionary<string, long>();
        foreach (var f in files)
            foreach (var kv in MogCoverage.Measure(File.ReadAllBytes(f)).GapsByRegion)
            { agg.TryAdd(kv.Key, 0); agg[kv.Key] += kv.Value; }
        var big = files.OrderByDescending(x => new FileInfo(x).Length).First();
        var bc = MogCoverage.Measure(File.ReadAllBytes(big));
        Console.WriteLine($"  largest undecoded runs in {Path.GetFileName(big)}:");
        foreach (var (rat, rlen, head) in bc.TopRealRuns.Take(8))
            Console.WriteLine($"    {rat:x8}  {rlen,6:N0} B   {head}");
        Console.WriteLine("  unmodelled bytes by region:");
        foreach (var kv in agg.OrderByDescending(x => x.Value))
            Console.WriteLine($"    {kv.Value,10:N0}  {kv.Key}");
        if (total > 0)
            Console.WriteLine($"  corpus: model accounts for {100.0 * covered / total:0.0}% of "
                            + $"{total / 1024:N0} KB — the rest is carried by the base64 image");
        return (pass, fail);
    }
}
