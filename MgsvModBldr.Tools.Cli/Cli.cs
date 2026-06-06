using System.Diagnostics;
using System.Security.Cryptography;
using MgsvModBldr.Tools.Fox;
using MgsvModBldr.Tools.Fsop;
using MgsvModBldr.Tools.Ftex;
using MgsvModBldr.Tools.Qar;
using MgsvModBldr.Tools.Translation;
using MgsvModBldr.Tools.Tests;

namespace MgsvModBldr.Tools.Cli;

// modbldr-tools wrapper DLL — single-arg CLI dispatch for the ported
// tools. The thin modbldr-tools(.exe) launcher just forwards argv here.
// Mirrors the drag-on-exe convention of the reference tools:
//
//   tools.exe <file.fox2>      -> file.fox2.xml          (decompile)
//   tools.exe <file.fox2.xml>  -> file.fox2              (compile)
//   tools.exe <file.fsop>      -> <basename>_unpacked/   (unpack)
//   tools.exe <unpacked_dir>   -> <basename>.fsop        (pack)
//   tools.exe test             -> run automated regression on cached fixtures
//   tools.exe test --harvest   -> refresh fixtures from Z:\ (uses datfpk)
//
// Optional --roundtrip flag does <op>-><inverse op> and SHA256-checks
// the result against the original. PASS only expected for tools whose
// reference is deterministic (FSOP); lossy ports (Fox) FAIL by design.
// Use `test` for the real per-tool gate.
public static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        // Debug: print game PathCode for a path (compare against archive hashes).
        if (args[0] == "pathcode")
        {
            foreach (var p in args.Skip(1))
                Console.WriteLine($"{MgsvModBldr.Tools.GameHashing.GameHash.PathCode(p):x16}  {p}");
            return 0;
        }
        if (args[0] == "stringid")
        {
            foreach (var p in args.Skip(1))
                Console.WriteLine($"{MgsvModBldr.Tools.GameHashing.GameHash.StringId(p):x16}  {p}");
            return 0;
        }
        // buildmgsv <sourceDir> <out.mgsv> — exercise the full ModBuilder
        // pipeline with the in-process managed FPK archiver (no datfpk).
        if (args[0] == "buildmgsv")
        {
            if (args.Length < 3) { Console.Error.WriteLine("usage: buildmgsv <sourceDir> <out.mgsv>"); return 2; }
            var meta = MgsvModBldr.Core.ModMetadata.Load(Path.Combine(args[1], "metadata.xml"));
            var builder = new MgsvModBldr.Core.ModBuilder
            {
                FpkArchiver = new MgsvModBldr.Tools.Fpk.ManagedFpkArchiver(),
                Log = Console.WriteLine,
            };
            builder.Build(args[1], meta, args[2]);
            return 0;
        }

        // `test` is a subcommand, not a file path — handle before file checks.
        //   test                  -> run everything
        //   test <tool>           -> run just that tool (fsop|fox|ftex)
        //   test [<tool>] --harvest -> refresh fixtures first (scope honours <tool> too)
        if (args[0] == "test")
        {
            bool harvest = args.Contains("--harvest") || args.Contains("-h");
            // Anything other than the flags is treated as a tool filter.
            var filter = args.Skip(1)
                             .FirstOrDefault(a => !a.StartsWith("--") && a != "-h");
            return TestRunner.Run(harvest, filter);
        }

        bool roundtrip = false;
        var positional = new List<string>();
        foreach (var a in args)
        {
            if (a == "--roundtrip" || a == "-r") roundtrip = true;
            else positional.Add(a);
        }

        if (positional.Count == 0)
        {
            PrintUsage();
            return 2;
        }

        var input = positional[0];
        if (!File.Exists(input) && !Directory.Exists(input))
        {
            Console.Error.WriteLine($"Input does not exist: {input}");
            return 2;
        }

        try
        {
            return Dispatch(input, roundtrip);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  tools.exe <file>           Auto-detect by extension and convert.");
        Console.WriteLine("  tools.exe --roundtrip <f>  <op>-><inverse> and SHA-check (PASS only for deterministic refs).");
        Console.WriteLine("  tools.exe test             Run automated regression on cached fixtures.");
        Console.WriteLine("  tools.exe test <tool>      Same, but only for one tool (fsop|fox|ftex|qar|fpk|pftxs|subp).");
        Console.WriteLine("  tools.exe test --harvest   Refresh fixtures from Z:\\ first (needs datfpk in builder.xml).");
        Console.WriteLine("  tools.exe test <tool> --harvest   Refresh just that tool's fixtures.");
        Console.WriteLine();
        Console.WriteLine("Supported inputs:");
        Console.WriteLine("  .fox2 .bnd .clo .des .evf .fsd .lad .parts .ph .phsd .sdf .sim .tgt .vdp .veh .vfxlf");
        Console.WriteLine("      -> writes <file>.xml      (decompile)");
        Console.WriteLine("  *.xml                       -> writes the stripped-extension binary back");
        Console.WriteLine("  .fsop                       -> writes <basename>_unpacked/  with metadata.json + .fxc files");
        Console.WriteLine("  any folder with metadata.json   -> writes <basename>.fsop  next to the folder");
        Console.WriteLine("  .subp                       -> writes <name>.subp.xml   (decompile subtitle pack)");
        Console.WriteLine("  *.subp.xml                  -> writes <name>.subp        (recompile)");
    }

    private static int Dispatch(string input, bool roundtrip)
    {
        if (Directory.Exists(input))
        {
            // Folder -> only FSOP pack-mode honours folders right now.
            return roundtrip ? RoundtripFsop(input) : PackFsop(input);
        }

        var ext = Path.GetExtension(input).ToLowerInvariant();
        if (ext == ".xml")
        {
            // Format-suffixed companion (like .fpk.json) — route before Fox's bare .xml.
            if (input.EndsWith(".subp.xml", StringComparison.OrdinalIgnoreCase))
            { var p = SubpConverter.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }
            return roundtrip ? RoundtripFoxFromXml(input) : CompileFox(input);
        }

        if (ext == ".subp") { var p = SubpConverter.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }

        if (FoxPacker.DecompilableExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return roundtrip ? RoundtripFox(input) : DecompileFox(input);

        if (ext == ".fsop")
            return roundtrip ? RoundtripFsopFromFile(input) : UnpackFsop(input);

        if (ext == ".ftex") { var p = FtexPacker.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".dds")  { var p = FtexPacker.Pack(input);   Console.WriteLine($"Packed    {input} -> {p}"); return 0; }

        if (ext == ".ftexs")
        {
            // .ftexs are mipmap sidecars of a .ftex — you unpack the
            // .ftex (which pulls in its sidecars automatically), not the
            // sidecar directly.
            var stem = Path.GetFileNameWithoutExtension(input);
            // foo.2.ftexs -> base is "foo" (strip the .N too)
            var dotIdx = stem.IndexOf('.');
            if (dotIdx > 0) stem = stem[..dotIdx];
            var ftex = Path.Combine(Path.GetDirectoryName(input) ?? ".", stem + ".ftex");
            Console.Error.WriteLine($".ftexs is a mipmap sidecar, not a standalone file.");
            Console.Error.WriteLine($"Unpack its .ftex instead:  modbldr-tools \"{ftex}\"");
            return 2;
        }

        if (ext == ".pftxs") { var p = MgsvModBldr.Tools.Pftxs.PftxsPacker.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".json" && input.EndsWith(".pftxs.json", StringComparison.OrdinalIgnoreCase))
        { var p = MgsvModBldr.Tools.Pftxs.PftxsPacker.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }

        if (ext == ".fpk" || ext == ".fpkd") { var p = MgsvModBldr.Tools.Fpk.FpkPacker.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".json" && (input.EndsWith(".fpk.json", StringComparison.OrdinalIgnoreCase) || input.EndsWith(".fpkd.json", StringComparison.OrdinalIgnoreCase)))
        { var p = MgsvModBldr.Tools.Fpk.FpkPacker.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }

        if (ext == ".dat" || ext == ".qar") { var p = QarPacker.Unpack(input); Console.WriteLine($"Unpacked  {input} -> {p}"); return 0; }
        if (ext == ".json" && (input.EndsWith(".dat.json", StringComparison.OrdinalIgnoreCase) || input.EndsWith(".qar.json", StringComparison.OrdinalIgnoreCase)))
        { var p = QarPacker.Pack(input); Console.WriteLine($"Packed    {input} -> {p}"); return 0; }

        Console.Error.WriteLine($"Unknown extension '{ext}'. See --help.");
        return 2;
    }

    // ─── Direct (one-shot) ops ──────────────────────────────────────────────

    private static int DecompileFox(string fox)
    {
        var sw = Stopwatch.StartNew();
        var outPath = FoxPacker.Decompile(fox);
        sw.Stop();
        Console.WriteLine($"Decompiled {fox} -> {outPath} ({sw.ElapsedMilliseconds} ms)");
        return 0;
    }

    private static int CompileFox(string xml)
    {
        var sw = Stopwatch.StartNew();
        var outPath = FoxPacker.Compile(xml);
        sw.Stop();
        Console.WriteLine($"Compiled   {xml} -> {outPath} ({sw.ElapsedMilliseconds} ms)");
        return 0;
    }

    private static int UnpackFsop(string fsop)
    {
        var dir = Path.Combine(Path.GetDirectoryName(fsop) ?? ".", Path.GetFileNameWithoutExtension(fsop) + "_unpacked");
        var sw = Stopwatch.StartNew();
        int n = FsopPacker.Unpack(fsop, dir);
        sw.Stop();
        Console.WriteLine($"Unpacked   {fsop} -> {dir} ({n} shaders, {sw.ElapsedMilliseconds} ms)");
        return 0;
    }

    private static int PackFsop(string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
        if (name.EndsWith("_unpacked")) name = name[..^"_unpacked".Length];
        var outPath = Path.Combine(Path.GetDirectoryName(dir) ?? ".", name + ".fsop");
        var sw = Stopwatch.StartNew();
        int n = FsopPacker.Pack(dir, outPath);
        sw.Stop();
        Console.WriteLine($"Packed     {dir} -> {outPath} ({n} shaders, {sw.ElapsedMilliseconds} ms)");
        return 0;
    }

    // ─── Round-trip regression gates ────────────────────────────────────────

    private static int RoundtripFox(string fox)
    {
        var work = MakeTmpDir("fox_rt_");
        var xml      = Path.Combine(work, Path.GetFileName(fox) + ".xml");
        var repacked = Path.Combine(work, Path.GetFileName(fox));
        var original = File.ReadAllBytes(fox);
        Console.WriteLine($"Input: {fox} ({original.Length:N0} bytes)");
        FoxPacker.Decompile(fox, xml);
        FoxPacker.Compile(xml, repacked);
        return CompareAndReport(original, File.ReadAllBytes(repacked), work);
    }

    private static int RoundtripFoxFromXml(string xml)
    {
        // For symmetry: compile XML -> decompile -> compare XML strings.
        var work     = MakeTmpDir("fox_rt_");
        var binPath  = Path.Combine(work, Path.GetFileNameWithoutExtension(xml));
        var xml2     = Path.Combine(work, Path.GetFileName(xml));
        FoxPacker.Compile(xml, binPath);
        FoxPacker.Decompile(binPath, xml2);
        var a = File.ReadAllText(xml);
        var b = File.ReadAllText(xml2);
        if (a == b) { Console.WriteLine("PASS: XML->bin->XML stable."); return 0; }
        Console.Error.WriteLine($"FAIL: XML differs. See {work}");
        return 1;
    }

    private static int RoundtripFsopFromFile(string fsop)
    {
        var work      = MakeTmpDir("fsop_rt_");
        var unpackDir = Path.Combine(work, "unpacked");
        var repacked  = Path.Combine(work, Path.GetFileName(fsop));
        var original  = File.ReadAllBytes(fsop);
        Console.WriteLine($"Input: {fsop} ({original.Length:N0} bytes)");
        FsopPacker.Unpack(fsop, unpackDir);
        FsopPacker.Pack(unpackDir, repacked);
        return CompareAndReport(original, File.ReadAllBytes(repacked), work);
    }

    private static int RoundtripFsop(string dir)
    {
        // Pack the folder, then unpack the result, then pack again, then
        // compare the two packs. Sanity check for hand-authored folders.
        var work    = MakeTmpDir("fsop_rt_");
        var a       = Path.Combine(work, "first.fsop");
        var unpack2 = Path.Combine(work, "unpacked2");
        var b       = Path.Combine(work, "second.fsop");
        FsopPacker.Pack(dir, a);
        FsopPacker.Unpack(a, unpack2);
        FsopPacker.Pack(unpack2, b);
        return CompareAndReport(File.ReadAllBytes(a), File.ReadAllBytes(b), work);
    }

    private static int CompareAndReport(byte[] original, byte[] repacked, string workDirOnFail)
    {
        if (original.Length != repacked.Length)
        {
            Console.Error.WriteLine($"FAIL: size differs (orig={original.Length}, repacked={repacked.Length})");
            Console.Error.WriteLine($"  See {workDirOnFail}");
            return 1;
        }
        var origHash = Convert.ToHexString(SHA256.HashData(original));
        var rpkdHash = Convert.ToHexString(SHA256.HashData(repacked));
        Console.WriteLine($"Original  SHA : {origHash}");
        Console.WriteLine($"Repacked  SHA : {rpkdHash}");
        if (origHash != rpkdHash)
        {
            int firstDiff = -1;
            for (int i = 0; i < original.Length; i++)
                if (original[i] != repacked[i]) { firstDiff = i; break; }
            Console.Error.WriteLine($"FAIL: hashes differ. First diff at 0x{firstDiff:X} (orig=0x{original[firstDiff]:X2}, repacked=0x{repacked[firstDiff]:X2})");
            Console.Error.WriteLine($"  See {workDirOnFail}");
            return 1;
        }
        Console.WriteLine("PASS: byte-exact.");
        return 0;
    }

    private static string MakeTmpDir(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }
}
