// In-process Core.IFpkArchiver backed by the managed FpkFile
using MgsvModBldr.Core;

namespace MgsvModBldr.Tools.Fpk;

public sealed class ManagedFpkArchiver : IFpkArchiver
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
