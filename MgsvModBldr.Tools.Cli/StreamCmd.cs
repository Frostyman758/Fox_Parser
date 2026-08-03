// stream verb: pull one file out of an archive
using MgsvModBldr.Tools.Streaming;

namespace MgsvModBldr.Tools.Cli;

// stream — extract ONE entry without unpacking the archive. The index is parsed,
// one block is decoded, nothing else is touched.
//
//   stream <archive.dat|.g0s> <path-or-hash> [-o <outfile>]
//   stream <archive.dat|.g0s> --list [substring]
//   stream --game <gameDir> <path> [-o <outfile>]
//
// <path> may continue through a nested .fpk/.fpkd:
//   stream data1.dat "Assets/tpp/pack/mission2/common/init.fpk/Assets/tpp/script/init.lua"
internal static class StreamCmd
{
    public static int Run(string[] args)
    {
        var rest = new List<string>();
        string outFile = null, gameDir = null, listFilter = null;
        bool list = false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--out" when i + 1 < args.Length: outFile = args[++i]; break;
                case "--game" when i + 1 < args.Length: gameDir = args[++i]; break;
                case "--list" or "-l":
                    list = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-")) listFilter = args[++i];
                    break;
                default: rest.Add(args[i]); break;
            }
        }

        try
        {
            if (gameDir is not null) return FromGame(gameDir, rest, outFile);
            if (rest.Count == 0) { Usage(); return 2; }
            var archive = rest[0];
            if (!File.Exists(archive)) { Console.Error.WriteLine($"FOXDIE: no such archive: {archive}"); return 2; }
            var kind = ArchiveFormat.Detect(archive);
            if (!ArchiveFormat.IsArchive(kind))
            {
                // .dat/.g0s that are really movies (master\e2f*.dat, GZ data_00.g0s).
                Console.Error.WriteLine($"FOXDIE: {ArchiveFormat.Describe(archive, kind)}.");
                return 2;
            }
            if (list) return List(archive, listFilter);
            if (rest.Count < 2) { Usage(); return 2; }
            return One(archive, rest[1], outFile);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FOXDIE: {ex.Message}");
            return 1;
        }
    }

    private static int List(string archive, string filter)
    {
        int n = 0;
        foreach (var it in ArchiveStream.List(archive))
        {
            var name = string.IsNullOrEmpty(it.Path) ? $"({it.Hash:x16})" : it.Path;
            if (filter is not null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            Console.WriteLine($"{it.Hash:x16}  {it.Size,12:N0}  {name}");
            n++;
        }
        Console.Error.WriteLine($"{n} entries.");
        return 0;
    }

    private static int One(string archive, string target, string outFile)
    {
        byte[] data = ulong.TryParse(target, System.Globalization.NumberStyles.HexNumber, null, out var hash) && target.Length == 16
            ? ArchiveStream.Read(archive, hash)
            : ArchiveStream.Read(archive, target);
        return WriteOut(data, target, outFile);
    }

    private static int FromGame(string gameDir, List<string> rest, string outFile)
    {
        if (rest.Count == 0) { Usage(); return 2; }
        var gs = GameStream.Open(gameDir);
        var homes = gs.Homes(rest[0]);
        if (homes.Count == 0) { Console.Error.WriteLine($"FOXDIE: {rest[0]} is not in that install."); return 2; }
        foreach (var h in homes) Console.Error.WriteLine($"  in {Path.GetFileName(h)}");
        return WriteOut(gs.Read(rest[0]), rest[0], outFile);
    }

    private static int WriteOut(byte[] data, string target, string outFile)
    {
        outFile ??= Path.GetFileName(target.Replace('\\', '/').TrimEnd('/'));
        var dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(outFile, data);
        Console.WriteLine($"Streamed  {target} -> {outFile} ({data.Length:N0} bytes)");
        return 0;
    }

    private static void Usage()
    {
        Console.Error.WriteLine("usage: stream <archive.dat|.g0s> <path-or-hash> [-o <outfile>]");
        Console.Error.WriteLine("       stream <archive.dat|.g0s> --list [substring]");
        Console.Error.WriteLine("       stream --game <gameDir> <path> [-o <outfile>]");
    }
}
