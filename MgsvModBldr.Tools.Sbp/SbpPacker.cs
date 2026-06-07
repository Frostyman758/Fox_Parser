using System.Text.Json;
using System.Text.Json.Serialization;

namespace MgsvModBldr.Tools.Sbp;

/// <summary>
/// Unpack/pack for .sbp sound-bank packages. Unpack writes a small JSON
/// manifest (<c>&lt;name&gt;.sbp.json</c>) plus the extracted sub-files into
/// <c>&lt;name&gt;_sbp/</c>; pack reads the manifest + folder back into a
/// byte-exact .sbp. Sub-file extraction is parallel.
/// </summary>
public static class SbpPacker
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Unpack(string sbpPath, string? outDir = null)
    {
        var sbp = new SbpFile();
        sbp.ReadFrom(sbpPath);

        outDir ??= DefaultDir(sbpPath);
        Directory.CreateDirectory(outDir);

        string entity = Path.GetFileNameWithoutExtension(sbpPath);

        // Assign output names sequentially (GzsTool-style "<entity>.<magic>",
        // disambiguated if two entries share a tag) so the manifest order is
        // authoritative; then write the payloads in parallel.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifest = new SbpManifest();
        var plan = new List<(SbpEntry e, string file)>();
        foreach (var e in sbp.Entries)
        {
            string ext = e.Magic.Length == 0 ? "bin" : e.Magic;
            string name = entity + "." + ext;
            for (int n = 1; !used.Add(name); n++)
                name = entity + "_" + n + "." + ext;
            plan.Add((e, name));
            manifest.Entries.Add(new SbpManifestEntry { Magic = e.Magic, File = name });
        }

        Parallel.ForEach(plan, p => File.WriteAllBytes(Path.Combine(outDir, p.file), p.e.Data));

        var manifestPath = sbpPath + ".json";
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOpts));
        return manifestPath;
    }

    public static string Pack(string manifestPath, string? outFile = null)
    {
        var manifest = JsonSerializer.Deserialize<SbpManifest>(File.ReadAllText(manifestPath), JsonOpts)
                       ?? throw new InvalidDataException("SBP manifest deserialise failed.");
        outFile ??= StripJson(manifestPath);
        var baseDir = ContentDirFor(manifestPath);

        var sbp = new SbpFile();
        foreach (var me in manifest.Entries)
        {
            sbp.Entries.Add(new SbpEntry
            {
                Magic = me.Magic,
                Data  = File.ReadAllBytes(Path.Combine(baseDir, me.File)),
            });
        }

        using var fs = File.Open(outFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        sbp.Write(fs);
        return outFile;
    }

    private static string DefaultDir(string sbpPath) =>
        Path.Combine(Path.GetDirectoryName(sbpPath) ?? ".",
                     Path.GetFileNameWithoutExtension(sbpPath) + "_sbp");

    private static string ContentDirFor(string manifestPath)
    {
        var name = Path.GetFileName(manifestPath);
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
        var stem = Path.GetFileNameWithoutExtension(name);
        return Path.Combine(Path.GetDirectoryName(manifestPath) ?? ".", stem + "_sbp");
    }

    private static string StripJson(string p) =>
        p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? p[..^5] : p + ".out";
}

public sealed class SbpManifest
{
    [JsonPropertyName("type")]    public string Type { get; set; } = "sbp";
    [JsonPropertyName("entries")] public List<SbpManifestEntry> Entries { get; set; } = new();
}

public sealed class SbpManifestEntry
{
    [JsonPropertyName("magic")] public string Magic { get; set; } = "";
    [JsonPropertyName("file")]  public string File  { get; set; } = "";
}
