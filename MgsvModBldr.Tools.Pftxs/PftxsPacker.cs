// Based on GzsTool.Core/Pftxs + AutoPftxsTool/ArchiveHandler.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using MgsvModBldr.Tools.Qar;

namespace MgsvModBldr.Tools.Pftxs;

public static class PftxsPacker
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Unpack(string pftxsPath, string? outDir = null, string? dictPath = null)
    {
        var dict = QarDictionary.Load(dictPath);
        outDir ??= DefaultUnpackDir(pftxsPath);
        Directory.CreateDirectory(outDir);

        var pftxs = new PftxsFile();
        pftxs.ReadFrom(pftxsPath);

        var allEntries = pftxs.Groups.SelectMany(g => g.Entries).ToList();
        Parallel.ForEach(allEntries, e =>
        {
            var path = dict.Resolve(e.Hash, out var resolved);
            e.FilePath = path;
            e.Resolved = resolved;
            var rel = path.TrimStart('/').Replace('\\', '/');
            var outFile = Path.Combine(outDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
            File.WriteAllBytes(outFile, e.Data);
        });

        var manifest = new PftxsManifest
        {
            Groups = pftxs.Groups.Select(g => new PftxsManifestGroup
            {
                Hash    = g.Entries.Count > 0 && g.Entries[0].Resolved ? 0 : g.Hash,
                Entries = g.Entries.Select(e => new PftxsManifestEntry
                {
                    FilePath = e.FilePath,
                    Hash     = e.Resolved ? 0 : e.Hash,
                }).ToList(),
            }).ToList(),
        };

        var manifestPath = pftxsPath + ".json";
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOpts));
        return manifestPath;
    }

    public static string Pack(string manifestPath, string? outFile = null)
    {
        var manifest = JsonSerializer.Deserialize<PftxsManifest>(File.ReadAllText(manifestPath), JsonOpts)
                       ?? throw new InvalidDataException("PFTXS manifest deserialise failed.");
        outFile ??= StripJson(manifestPath);
        var baseDir = ContentDirFor(manifestPath);

        var pftxs = new PftxsFile();
        foreach (var mg in manifest.Groups)
        {
            var g = new PftxsGroup { Hash = mg.Hash };
            foreach (var me in mg.Entries)
            {
                var rel = me.FilePath.TrimStart('/').Replace('\\', '/');
                var e = new PftxsEntry
                {
                    FilePath = me.FilePath,
                    Hash     = me.Hash != 0 ? me.Hash : GameHashing.GameHash.PathCode(me.FilePath),
                    Data     = File.ReadAllBytes(Path.Combine(baseDir, rel)),
                };
                g.Entries.Add(e);
            }
            if (g.Hash == 0 && g.Entries.Count > 0) g.Hash = g.Entries[0].Hash;
            pftxs.Groups.Add(g);
        }

        using var fs = File.Open(outFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        pftxs.Write(fs);
        return outFile;
    }

    private static string DefaultUnpackDir(string p)
    {
        var stem = Path.GetFileNameWithoutExtension(p);
        var dir  = Path.GetDirectoryName(p) ?? ".";
        return Path.Combine(dir, stem + "_pftxs");
    }

    private static string ContentDirFor(string manifestPath)
    {
        var name = Path.GetFileName(manifestPath);
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
        var stem = Path.GetFileNameWithoutExtension(name);
        var dir  = Path.GetDirectoryName(manifestPath) ?? ".";
        return Path.Combine(dir, stem + "_pftxs");
    }

    private static string StripJson(string p)
        => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? p[..^5] : p + ".out";
}

public sealed class PftxsManifest
{
    [JsonPropertyName("type")]   public string Type { get; set; } = "pftxs";
    [JsonPropertyName("groups")] public List<PftxsManifestGroup> Groups { get; set; } = new();
}

public sealed class PftxsManifestGroup
{
    [JsonPropertyName("hash")]    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public ulong Hash { get; set; }
    [JsonPropertyName("entries")] public List<PftxsManifestEntry> Entries { get; set; } = new();
}

public sealed class PftxsManifestEntry
{
    [JsonPropertyName("filePath")] public string FilePath { get; set; } = "";
    [JsonPropertyName("hash")]     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public ulong Hash { get; set; }
}
