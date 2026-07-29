// Based on datfpk cli/main.go ExtractFpk/PackFpk
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MgsvModBldr.Tools.Fpk;

public static class FpkPacker
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Unpack(string fpkPath, string? outDir = null)
    {
        // GZ (ste platform tag) routes to the read-only GZ unpacker.
        Span<byte> head = stackalloc byte[10];
        using (var sniff = File.OpenRead(fpkPath))
            if (sniff.Read(head) == head.Length && Gz.GzFpkFile.IsGzMagic(head))
                return Gz.GzFpkUnpacker.Unpack(fpkPath, outDir);

        outDir ??= DefaultUnpackDir(fpkPath);
        Directory.CreateDirectory(outDir);

        var fpk = new FpkFile();
        fpk.ReadFrom(fpkPath);

        Parallel.ForEach(fpk.Entries, e =>
        {
            using var fs = File.OpenRead(fpkPath);
            e.ReadData(fs);
            var rel = e.FilePath.Data.TrimStart('/').Replace('\\', '/');
            var outFile = Path.Combine(outDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
            File.WriteAllBytes(outFile, e.Data);
        });

        var manifest = new FpkManifest
        {
            Type    = fpk.IsFpkd ? "fpkd" : "fpk",
            Entries = fpk.Entries.Select(e => new FpkManifestEntry
            {
                FilePath  = e.FilePath.Data,
                Encrypted = e.Encrypted,
            }).ToList(),
            References = fpk.References.Select(r => new FpkManifestRef { FilePath = r.Data }).ToList(),
        };

        var manifestPath = fpkPath + ".json";
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOpts));
        return manifestPath;
    }

    public static string Pack(string manifestPath, string? outFile = null)
    {
        var manifest = JsonSerializer.Deserialize<FpkManifest>(File.ReadAllText(manifestPath), JsonOpts)
                       ?? throw new InvalidDataException("FPK manifest deserialise failed.");

        if (manifest.Type.StartsWith("gz", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GZ fpk(d) is read-only — packing GZ archives is not supported.");

        outFile ??= StripJson(manifestPath);
        var baseDir = ContentDirFor(manifestPath);

        var fpk = new FpkFile();
        fpk.SetType(string.Equals(manifest.Type, "fpkd", StringComparison.OrdinalIgnoreCase));

        foreach (var me in manifest.Entries)
        {
            var e = new FpkEntry { Encrypted = me.Encrypted };
            e.FilePath.Data = me.FilePath;
            fpk.Entries.Add(e);
        }
        foreach (var mr in manifest.References)
            fpk.References.Add(new FpkString { Data = mr.FilePath });

        using var fs = File.Open(outFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        fpk.Write(fs, baseDir);
        return outFile;
    }

    internal static string DefaultUnpackDir(string fpkPath)
    {
        var stem = Path.GetFileNameWithoutExtension(fpkPath);
        var ext  = Path.GetExtension(fpkPath).TrimStart('.');
        var dir  = Path.GetDirectoryName(fpkPath) ?? ".";
        return Path.Combine(dir, $"{stem}_{ext}");
    }

    private static string ContentDirFor(string manifestPath)
    {
        var name = Path.GetFileName(manifestPath);
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext  = Path.GetExtension(name).TrimStart('.');
        var dir  = Path.GetDirectoryName(manifestPath) ?? ".";
        return Path.Combine(dir, $"{stem}_{ext}");
    }

    private static string StripJson(string p)
        => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? p[..^5] : p + ".out";
}

public sealed class FpkManifest
{
    [JsonPropertyName("type")]       public string Type { get; set; } = "fpk";
    [JsonPropertyName("entries")]    public List<FpkManifestEntry> Entries { get; set; } = new();
    [JsonPropertyName("references")] public List<FpkManifestRef> References { get; set; } = new();
}

public sealed class FpkManifestEntry
{
    [JsonPropertyName("filePath")]  public string FilePath  { get; set; } = "";
    [JsonPropertyName("encrypted")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Encrypted { get; set; }
    // GZ only: name not in fpk_dictionary, path synthesised from the MD5.
    [JsonPropertyName("unresolved")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Unresolved { get; set; }
}

public sealed class FpkManifestRef
{
    [JsonPropertyName("filePath")] public string FilePath { get; set; } = "";
}
