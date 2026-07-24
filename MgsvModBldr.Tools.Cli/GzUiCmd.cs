// gzui verb: convert GZ UI files to TPP
// 09/07/2026
using MgsvModBldr.Tools.Ui.Uif;
using MgsvModBldr.Tools.Ui.Uigb;
using MgsvModBldr.Tools.Ui.Uilb;

namespace MgsvModBldr.Tools.Cli;

/// <summary>
/// `modbldr-tools gzui &lt;file|folder&gt; [-o out]` — GZ→TPP UI conversion.
/// uilb converts; uia copies (format identical); uigb/uif pending.
/// </summary>
public static class GzUiCmd
{
    public static int Run(string[] args)
    {
        string input = null, outArg = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] is "-o" or "--out") { outArg = args[++i]; continue; }
            input ??= args[i];
        }
        if (input == null) { Console.Error.WriteLine("usage: gzui <file|folder> [-o out]"); return 2; }
        if (Directory.Exists(input))
        {
            var outDir = outArg ?? input + "_tpp";
            Directory.CreateDirectory(outDir);
            int ok = 0, skip = 0, fail = 0;
            foreach (var f in Directory.EnumerateFiles(input, "*.*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(input, f);
                var dst = Path.Combine(outDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                switch (One(f, dst)) { case 0: ok++; break; case 1: skip++; break; default: fail++; break; }
            }
            Console.WriteLine($"gzui: {ok} converted, {skip} skipped, {fail} failed -> {outDir}");
            return fail == 0 ? 0 : 1;
        }
        if (!File.Exists(input)) { Console.Error.WriteLine($"FOXDIE: no such file {input}"); return 2; }
        var outPath = outArg ?? DefaultOut(input);
        int r = One(input, outPath);
        if (r == 0) Console.WriteLine($"Converted {input} -> {outPath}");
        return r == 0 ? 0 : 1;
    }

    static string DefaultOut(string f)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(f));
        return Path.Combine(dir!, Path.GetFileNameWithoutExtension(f) + "_tpp" + Path.GetExtension(f));
    }

    // 0 converted/copied, 1 skipped, 2 failed
    static int One(string src, string dst)
    {
        var ext = Path.GetExtension(src).ToLowerInvariant();
        try
        {
            switch (ext)
            {
                case ".uilb":
                    var gz = UilbReader.Read(File.ReadAllBytes(src));
                    if (gz.IsTpp) { Console.Error.WriteLine($"skip (already TPP): {src}"); return 1; }
                    File.WriteAllBytes(dst, UilbWriter.Write(UilbConvert.GzToTpp(gz)));
                    return 0;
                case ".uia":
                    File.Copy(src, dst, overwrite: true);   // format identical GZ/TPP
                    return 0;
                case ".uigb":
                    var g = UigbReader.Read(File.ReadAllBytes(src));
                    if (g.IsTpp) { Console.Error.WriteLine($"skip (already TPP): {src}"); return 1; }
                    File.WriteAllBytes(dst, UigbWriter.Write(UigbConvert.GzToTpp(g)));
                    return 0;
                case ".uif":
                    if (GzUif.U32(File.ReadAllBytes(src), 4) == 0x202) { Console.Error.WriteLine($"skip (already TPP): {src}"); return 1; }
                    File.WriteAllBytes(dst, UifConvert.GzToTpp(File.ReadAllBytes(src), out var log));
                    foreach (var l in log) Console.Error.WriteLine($"  note: {Path.GetFileName(src)}: {l}");
                    return 0;
                default:
                    return 1;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"FOXDIE: {src}: {e.Message}");
            return 2;
        }
    }
}
