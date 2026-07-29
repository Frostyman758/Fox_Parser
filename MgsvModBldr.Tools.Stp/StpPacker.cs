// .stp/.sab unpack/repack facade
namespace MgsvModBldr.Tools.Stp;

public static class StpPacker
{
    public static string Unpack(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(path);
        var dir = Path.Combine(Path.GetDirectoryName(path) ?? ".", name + "_" + ext);
        Directory.CreateDirectory(dir);

        switch (ext)
        {
            case "stp":
                var stp = new StreamedPackage();
                stp.ReadFrom(path);
                Parallel.ForEach(stp.Entries, e =>
                {
                    if (e.Wem.Length > 0) File.WriteAllBytes(Path.Combine(dir, e.Name + ".wem"), e.Wem);
                    if (e.Ls2.Length > 0) File.WriteAllBytes(Path.Combine(dir, e.Name + ".ls2"), e.Ls2);
                });
                break;
            case "sab":
                var sab = new StreamedAnimation();
                sab.ReadFrom(path);
                Parallel.ForEach(sab.Entries, e =>
                {
                    if (e.Lsst.Length > 0) File.WriteAllBytes(Path.Combine(dir, e.Name + ".lsst"), e.Lsst);
                });
                break;
            default:
                throw new InvalidDataException($"Unsupported Stp extension '.{ext}' (expected .stp or .sab).");
        }
        return dir;
    }

    public static string Pack(string dir, StpVersion version = StpVersion.TPP)
    {
        dir = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        bool isSab = dir.EndsWith("sab", StringComparison.OrdinalIgnoreCase);
        var outFile = StripSuffix(dir, isSab ? "_sab" : "_stp") + (isSab ? ".sab" : ".stp");

        // Same enumeration the reference uses; NTFS returns these in a stable
        // (UTF-16 collated) order, so it matches the reference run-for-run.
        var files = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly);

        if (isSab) WriteSab(files, outFile);
        else WriteStp(files, version, outFile);
        return outFile;
    }

    private static void WriteStp(string[] files, StpVersion version, string outFile)
    {
        var stp = new StreamedPackage { Version = version };
        // Reference ImportFiles: one entry per .wem, in file order, with the
        // matching .ls2 (same basename) pulled in as a sidecar.
        var wems = files.Where(f => Path.GetExtension(f).Equals(".wem", StringComparison.OrdinalIgnoreCase)).ToArray();
        var loaded = new (uint name, byte[] wem, byte[] ls2)[wems.Length];
        Parallel.For(0, wems.Length, i =>
        {
            var f = wems[i];
            var ls2Path = Path.ChangeExtension(f, ".ls2");
            loaded[i] = (
                Convert.ToUInt32(Path.GetFileNameWithoutExtension(f)),
                File.ReadAllBytes(f),
                File.Exists(ls2Path) ? File.ReadAllBytes(ls2Path) : Array.Empty<byte>());
        });
        foreach (var (name, wem, ls2) in loaded)
            stp.Entries.Add(new StpEntry { Name = name, Wem = wem, Ls2 = ls2 });

        using var fs = File.Open(outFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        stp.Write(fs);
    }

    private static void WriteSab(string[] files, string outFile)
    {
        var sab = new StreamedAnimation();
        var lssts = files.Where(f => Path.GetExtension(f).Equals(".lsst", StringComparison.OrdinalIgnoreCase)).ToArray();
        var loaded = new (ulong name, byte[] data)[lssts.Length];
        Parallel.For(0, lssts.Length, i =>
            loaded[i] = (Convert.ToUInt64(Path.GetFileNameWithoutExtension(lssts[i])), File.ReadAllBytes(lssts[i])));
        foreach (var (name, data) in loaded)
            sab.Entries.Add(new SabEntry { Name = name, Lsst = data });

        using var fs = File.Open(outFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        sab.Write(fs);
    }

    private static string StripSuffix(string dir, string suffix) =>
        dir.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? dir[..^suffix.Length] : dir;
}
