// Based on FtexTool ZipUtility.cs (SharpZipLib -> ZLibStream)
using System.IO;
using System.IO.Compression;

namespace MgsvModBldr.Tools.Ftex
{
    internal static class ZipUtility
    {
        internal static byte[] Inflate(byte[] buffer)
        {
            using MemoryStream src = new MemoryStream(buffer);
            using ZLibStream  z   = new ZLibStream(src, CompressionMode.Decompress);
            using MemoryStream dst = new MemoryStream();
            z.CopyTo(dst);
            return dst.ToArray();
        }

        internal static byte[] Deflate(byte[] buffer)
        {
            using MemoryStream dst = new MemoryStream();
            using (ZLibStream z = new ZLibStream(dst, CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(buffer, 0, buffer.Length);
            }
            return dst.ToArray();
        }
    }
}
