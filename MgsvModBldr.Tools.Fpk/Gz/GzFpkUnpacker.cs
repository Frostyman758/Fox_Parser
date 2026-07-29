// GZ fpk(d) read-only unpack: folder + manifest
// 29/07/2026
using System.Text.Json;

namespace MgsvModBldr.Tools.Fpk.Gz;

public static class GzFpkUnpacker
{
    // Mirrors FpkPacker.Unpack output shape. No Pack counterpart: the GZ
    // reader is read-only by design (TPP packer stays byte-exact, untouched).
    public static string Unpack(string fpkPath, string? outDir = null)
    {
        outDir ??= FpkPacker.DefaultUnpackDir(fpkPath);
        Directory.CreateDirectory(outDir);

        GzFpkFile fpk;
        using (var fs = File.OpenRead(fpkPath)) fpk = GzFpkFile.Read(fs);

        foreach (var e in fpk.Entries)
        {
            var rel = e.FilePath.TrimStart('/').Replace('\\', '/');
            var outFile = Path.Combine(outDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
            File.WriteAllBytes(outFile, e.Data);
        }

        var manifest = new FpkManifest
        {
            Type = fpk.IsFpkd ? "gz-fpkd" : "gz-fpk",
            Entries = fpk.Entries.Select(e => new FpkManifestEntry
            {
                FilePath   = e.FilePath,
                Unresolved = !e.Resolved,
            }).ToList(),
        };

        var manifestPath = fpkPath + ".json";
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, FpkPacker.JsonOpts));
        return manifestPath;
    }
}
