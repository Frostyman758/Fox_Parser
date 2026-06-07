// Based on FoxTool Program.cs
using System.IO;

namespace MgsvModBldr.Tools.Fox;

public static class FoxPacker
{
    private static readonly object DictLock = new();
    private static Dictionary<ulong, string>? _cachedDictionary;

    public static string Decompile(string inputPath)
    {
        var outputPath = inputPath + ".xml";
        Decompile(inputPath, outputPath);
        return outputPath;
    }

    public static void Decompile(string inputPath, string outputPath)
    {
        var dict = LoadDictionary();
        var lookup = new FoxLookupTable(dict);

        using var input  = new FileStream(inputPath,  FileMode.Open,   FileAccess.Read);
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        var foxFile = FoxFile.ReadFoxFile(input, lookup);
        FoxConverter.DecompileFox(foxFile, output);
    }

    public static string Compile(string inputPath)
    {
        var outputPath = StripXmlExtension(inputPath);
        Compile(inputPath, outputPath);
        return outputPath;
    }

    public static void Compile(string inputPath, string outputPath)
    {
        using var input  = new FileStream(inputPath,  FileMode.Open,   FileAccess.Read);
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        FoxConverter.CompileFox(input, output);
    }

    public static IReadOnlyList<string> DecompilableExtensions { get; } = new[]
    {
        ".bnd", ".clo", ".des", ".evf", ".fox2", ".fsd", ".lad", ".parts",
        ".ph", ".phsd", ".sdf", ".sim", ".tgt", ".vdp", ".veh", ".vfxlf",
    };

    private static string StripXmlExtension(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var dir      = Path.GetDirectoryName(path) ?? string.Empty;
        return Path.Combine(dir, fileName);
    }

    public const string DictionaryFileName = "qar_dictionary.txt";

    private static string DefaultDictionaryPath()
    {
        // dict/ folder next to the exe, with loose-beside / CWD fallbacks.
        foreach (var p in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "dict", DictionaryFileName),
            Path.Combine(AppContext.BaseDirectory, DictionaryFileName),
            Path.Combine(Directory.GetCurrentDirectory(), "dict", DictionaryFileName),
            Path.Combine(Directory.GetCurrentDirectory(), DictionaryFileName),
        })
            if (File.Exists(p)) return p;
        return Path.Combine(AppContext.BaseDirectory, "dict", DictionaryFileName);
    }

    private static Dictionary<ulong, string> LoadDictionary()
    {
        if (_cachedDictionary is not null) return _cachedDictionary;
        lock (DictLock)
        {
            if (_cachedDictionary is not null) return _cachedDictionary;

            var path = DefaultDictionaryPath();
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Fox dictionary not found: {path}. Ship {DictionaryFileName} next to the executable.", path);

            var dict = new Dictionary<ulong, string>(capacity: 64 * 1024);
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                var hash = Hashing.HashString(line);
                dict.TryAdd(hash, line);
            }
            _cachedDictionary = dict;
            return dict;
        }
    }
}
