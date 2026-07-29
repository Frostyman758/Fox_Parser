// GZ pftxs read-only unpack: folder + manifest
// 29/07/2026
using System.Text.Json;

namespace MgsvModBldr.Tools.Pftxs.Gz;

public static class GzPftxsUnpacker
{
    // Mirrors PftxsPacker.Unpack output shape (single group, names carried
    // in the file itself — no hash dictionary). No Pack: GZ is read-only.
    public static string Unpack(string pftxsPath, string? outDir = null)
    {
        outDir ??= PftxsPacker.DefaultUnpackDir(pftxsPath);
        Directory.CreateDirectory(outDir);

        GzPftxsFile pftxs;
        using (var fs = File.OpenRead(pftxsPath)) pftxs = GzPftxsFile.Read(fs);

        foreach (var e in pftxs.Files)
        {
            var rel = e.Path.TrimStart('/').Replace('\\', '/');
            var outFile = Path.Combine(outDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
            File.WriteAllBytes(outFile, e.Data);
        }

        var manifest = new PftxsManifest
        {
            Type = "gz-pftxs",
            Groups =
            {
                new PftxsManifestGroup
                {
                    Entries = pftxs.Files.Select(e => new PftxsManifestEntry
                    {
                        FilePath = e.Path,
                    }).ToList(),
                },
            },
        };

        var manifestPath = pftxsPath + ".json";
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, PftxsPacker.JsonOpts));
        return manifestPath;
    }
}
