// vpath to native path resolver
// 07/07/2026
using System;

namespace MgsvModBldr.Tools.PathResolve;

// Port of fox::fs::GetRealPathWithLevel. level always passed explicitly
// (release for all real installs), so the engine's level-inference fallback
// is not ported - it never runs here.
public static class VirtualPath
{
    const string AssetsPrefix = "/Assets/";

    public static string ToNative(string vpath, string rootDir, string level = "release")
    {
        if (!vpath.StartsWith(AssetsPrefix, StringComparison.Ordinal))
            return vpath;

        if (!rootDir.EndsWith('/')) rootDir += "/";

        int prefixLen = AssetsPrefix.Length;
        if (vpath.Length <= prefixLen)
            return rootDir;

        int slashPos = vpath.IndexOf('/', prefixLen);
        if (slashPos < 0)
            return rootDir + vpath[prefixLen..];

        string project = vpath[prefixLen..slashPos];
        string result = rootDir + project + "/" + level;

        int dotPos = vpath.LastIndexOf('.');
        int lastSlashPos = vpath.LastIndexOf('/');
        if (dotPos < 0)
            return result + vpath[slashPos..];
        if (lastSlashPos < 0)
            return vpath;

        int fileNameStart = lastSlashPos + 1;
        if (slashPos < fileNameStart)
            result += vpath[slashPos..fileNameStart];

        string ext = dotPos + 1 < vpath.Length ? vpath[(dotPos + 1)..] : "";

        if (ExtensionCategories.HasTarget(ext))
        {
            string targetDir = ExtensionCategories.TargetDirectory(ext);
            if (targetDir.Length > 0) result += targetDir + "/";
        }
        if (ExtensionCategories.HasLanguage(ext))
        {
            string langDir = ExtensionCategories.LanguageDirectory;
            if (langDir.Length > 0) result += langDir + "/";
        }

        return result + vpath[fileNameStart..];
    }
}
