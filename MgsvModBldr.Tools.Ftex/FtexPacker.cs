// Based on FtexTool Program.cs
using System.IO;
using MgsvModBldr.Tools.Ftex.Dds;
using MgsvModBldr.Tools.Ftex.Exceptions;
using MgsvModBldr.Tools.Ftex.Ftex;
using MgsvModBldr.Tools.Ftex.Ftex.Enum;
using MgsvModBldr.Tools.Ftex.Ftexs;

namespace MgsvModBldr.Tools.Ftex;

public static class FtexPacker
{
    public static string Unpack(string ftexPath, string? outputDir = null)
    {
        var fileDir  = outputDir ?? Path.GetDirectoryName(ftexPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(ftexPath);
        var ftexFile = LoadFtex(ftexPath);
        var ddsFile  = FtexDdsConverter.ConvertToDds(ftexFile);
        var outPath  = Path.Combine(fileDir, $"{fileName}.dds");
        using (var os = new FileStream(outPath, FileMode.Create))
            ddsFile.Write(os);
        return outPath;
    }

    public static string Pack(
        string ddsPath,
        string? outputDir = null,
        FtexTextureType textureType = FtexTextureType.DiffuseMap,
        FtexUnknownFlags flags = FtexUnknownFlags.Default,
        int? ftexsFileCount = null)
    {
        var fileDir  = outputDir ?? Path.GetDirectoryName(ddsPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(ddsPath);

        DdsFile ddsFile;
        using (var ds = new FileStream(ddsPath, FileMode.Open, FileAccess.Read))
            ddsFile = DdsFile.Read(ds);

        var ftexFile = FtexDdsConverter.ConvertToFtex(ddsFile, textureType, flags, ftexsFileCount);
        var ftexPath = Path.Combine(fileDir, $"{fileName}.ftex");

        using (var ftexStream = new FileStream(ftexPath, FileMode.Create))
        {
            if (ftexsFileCount == 0) ftexStream.Seek(ftexFile.Size, SeekOrigin.Begin);

            foreach (var ftexs in ftexFile.FtexsFiles)
            {
                if (ftexsFileCount == 0)
                {
                    ftexs.Write(ftexStream);
                }
                else
                {
                    var ftexsName = $"{fileName}.{ftexs.FileNumber}.ftexs";
                    var ftexsPath = Path.Combine(fileDir, ftexsName);
                    using var os = new FileStream(ftexsPath, FileMode.Create);
                    ftexs.Write(os);
                }
            }

            ftexFile.UpdateOffsets();
            ftexStream.Seek(0, SeekOrigin.Begin);
            ftexFile.Write(ftexStream);
        }
        return ftexPath;
    }

    private static FtexFile LoadFtex(string ftexPath)
    {
        var fileDir  = Path.GetDirectoryName(ftexPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(ftexPath);

        FtexFile ftexFile;
        using (var fs = new FileStream(ftexPath, FileMode.Open, FileAccess.Read))
            ftexFile = FtexFile.ReadFtexFile(fs);

        for (byte n = 0; n <= ftexFile.FtexsFileCount; n++)
            ftexFile.AddFtexsFile(new FtexsFile { FileNumber = n });

        foreach (var mip in ftexFile.MipMapInfos)
        {
            var ftexsName = mip.FtexsFileNumber == 0
                ? Path.GetFileName(ftexPath)
                : $"{fileName}.{mip.FtexsFileNumber}.ftexs";
            var ftexsPath = Path.Combine(fileDir, ftexsName);
            if (ftexFile.TryGetFtexsFile(mip.FtexsFileNumber, out var ftexsFile)
                && File.Exists(ftexsPath))
            {
                using var fs = new FileStream(ftexsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Position = mip.Offset;
                ftexsFile.Read(fs, mip.ChunkCount, mip.Offset, mip.DecompressedFileSize);
            }
            else
            {
                throw new MissingFtexsFileException($"{ftexsName} not found");
            }
        }
        return ftexFile;
    }
}
