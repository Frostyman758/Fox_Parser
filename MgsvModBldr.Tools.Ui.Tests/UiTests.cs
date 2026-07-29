// UI format gates: round-trips + GZ→TPP conversion
// 09/07/2026
using MgsvModBldr.Tools.Testing;
using MgsvModBldr.Tools.Ui.Uigb;
using MgsvModBldr.Tools.Ui.Uilb;
using static MgsvModBldr.Tools.Testing.TestHelpers;
using static MgsvModBldr.Tools.Testing.TestEnv;

namespace MgsvModBldr.Tools.Ui.Tests;

public sealed class UiTests : IToolTests
{
    public string Name => "ui";
    public void Harvest() { }   // fixtures are a manual corpus, no Z:\ oracle

    static string Dir(string fmt, string ver) =>
        Path.Combine(FixturesDir, "ui", "ui format examples", fmt, $"PC {ver}");

    public (int pass, int fail) Run()
    {
        int pass = 0, fail = 0;
        foreach (var (tag, ver) in new[] { ("uilb GZ round-trip", "GZ"), ("uilb TPP round-trip", "TPP") })
        {
            Console.WriteLine($"--- {tag} ---");
            var files = List(Dir("uilb", ver), "*.uilb");
            if (files.Count == 0) { Console.WriteLine("  (no fixtures)"); continue; }
            var (p, f) = RunQuiet(files, RoundTrip);
            pass += p; fail += f;
        }
        Console.WriteLine("--- uilb GZ→TPP convert ---");
        var gz = List(Dir("uilb", "GZ"), "*.uilb");
        if (gz.Count > 0) { var (p, f) = RunQuiet(gz, Convert); pass += p; fail += f; }

        foreach (var (tag, ver) in new[] { ("uigb GZ round-trip", "GZ"), ("uigb TPP round-trip", "TPP") })
        {
            Console.WriteLine($"--- {tag} ---");
            var files = List(Dir("uigb", ver), "*.uigb");
            if (files.Count == 0) { Console.WriteLine("  (no fixtures)"); continue; }
            var (p, f) = RunQuiet(files, GraphRoundTrip);
            pass += p; fail += f;
        }
        Console.WriteLine("--- uigb GZ→TPP convert ---");
        var ggz = List(Dir("uigb", "GZ"), "*.uigb");
        if (ggz.Count > 0) { var (p, f) = RunQuiet(ggz, GraphConvert); pass += p; fail += f; }

        Console.WriteLine("--- uif GZ→TPP convert (geometry/refs equivalence) ---");
        var ugz = List(Dir("uif", "GZ"), "*.uif");
        if (ugz.Count > 0) { var (p, f) = RunQuiet(ugz, UifGate.Check); pass += p; fail += f; }
        return (pass, fail);
    }

    static List<string> List(string dir, string pattern) => !Directory.Exists(dir) ? new()
        : Directory.EnumerateFiles(dir, pattern).OrderBy(x => x).ToList();

    // suite RunParallel prints per file; 1000+ fixtures → only print failures
    static (int, int) RunQuiet(List<string> files, Func<string, (bool ok, string note)> gate)
    {
        var results = new (bool ok, string note)[files.Count];
        Parallel.For(0, files.Count, i => { try { results[i] = gate(files[i]); } catch (Exception e) { results[i] = (false, e.Message); } });
        int pass = 0, fail = 0;
        for (int i = 0; i < files.Count; i++)
        {
            if (!results[i].ok) { Console.WriteLine($"  [FAIL] {Path.GetFileName(files[i])} {results[i].note}"); fail++; }
            else pass++;
        }
        Console.WriteLine($"  {pass}/{files.Count} ok");
        return (pass, fail);
    }

    static (bool ok, string note) RoundTrip(string path)
    {
        var src = File.ReadAllBytes(path);
        var outBytes = UilbWriter.Write(UilbReader.Read(src));
        if (outBytes.Length != src.Length) return (false, $"len {outBytes.Length} != {src.Length}");
        for (int i = 0; i < src.Length; i++)
            if (src[i] != outBytes[i]) return (false, $"differs @0x{i:x}");
        return (true, "");
    }

    static (bool ok, string note) Convert(string path)
    {
        var gz = UilbReader.Read(File.ReadAllBytes(path));
        var tpp = UilbReader.Read(UilbWriter.Write(UilbConvert.GzToTpp(gz)));
        if (!tpp.IsTpp) return (false, "not tpp");
        if (tpp.IdCount != gz.IdCount || tpp.PathCount != gz.PathCount
            || tpp.ModelCount != gz.ModelCount || tpp.AnimCount != gz.AnimCount
            || tpp.CameraCount != gz.CameraCount || tpp.GraphCount != gz.GraphCount)
            return (false, "count drift");
        for (int i = 0; i < gz.IdCount; i++)
            if (tpp.TppIds[i] != (uint)gz.GzIds[i]) return (false, $"id {i}");
        return (true, "");
    }

    static (bool ok, string note) GraphRoundTrip(string path)
    {
        var src = File.ReadAllBytes(path);
        var outBytes = UigbWriter.Write(UigbReader.Read(src));
        if (outBytes.Length != src.Length) return (false, $"len {outBytes.Length} != {src.Length}");
        for (int i = 0; i < src.Length; i++)
            if (src[i] != outBytes[i]) return (false, $"differs @0x{i:x}");
        return (true, "");
    }

    static (bool ok, string note) GraphConvert(string path)
    {
        var gz = UigbReader.Read(File.ReadAllBytes(path));
        if (!gz.S4Absent) return (true, "skip (section4)");
        var tpp = UigbReader.Read(UigbWriter.Write(UigbConvert.GzToTpp(gz)));
        if (!tpp.IsTpp) return (false, "not tpp");
        if (tpp.Nodes.Count != gz.Nodes.Count || tpp.IdCount != gz.IdCount
            || tpp.PathCount != gz.PathCount || tpp.UilbCount != gz.UilbCount)
            return (false, "count drift");
        for (int i = 0; i < gz.IdCount; i++)
            if (tpp.TppIds[i] != (uint)gz.GzIds[i]) return (false, $"id {i}");
        for (int i = 0; i < gz.Nodes.Count; i++)
            if (tpp.Nodes[i].TypeIdx != gz.Nodes[i].TypeIdx || tpp.Nodes[i].Type != gz.Nodes[i].Type)
                return (false, $"node {i}");
        return (true, "");
    }
}
