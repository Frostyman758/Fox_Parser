// In-process fpk pack + enumerate via managed FpkFile
namespace MgsvModBldr.Tools.Fpk;

public readonly record struct FpkInnerFile(string Vpath, byte[] Data);

public sealed class ManagedFpkArchiver
{
    public void Pack(bool isFpkd, IReadOnlyList<string> entryVpaths, string contentDir, string outFile)
    {
        var fpk = new FpkFile();
        fpk.SetType(isFpkd);
        foreach (var vp in entryVpaths)
        {
            var e = new FpkEntry();
            e.FilePath.Data = vp;
            fpk.Entries.Add(e);
        }
        using var fs = File.Open(outFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        fpk.Write(fs, contentDir);
    }

    public IReadOnlyList<FpkInnerFile> ReadEntries(string fpkPath)
    {
        var fpk = new FpkFile();
        fpk.ReadFrom(fpkPath);
        var list = new List<FpkInnerFile>(fpk.Entries.Count);
        foreach (var e in fpk.Entries)
            list.Add(new FpkInnerFile(e.FilePath.Data, e.Data));
        return list;
    }
}
