// GZ pftxs reader (read-only)
// Separate from the TPP PftxsFile so the proven TPP path is untouched.
// Ported from GzsTool 0.2 (Pftxs/PftxsFile*, Psub/PsubFile*).
//
// GZ .pftxs has a different layout from TPP: a 20-byte PFTX header whose second
// word is the float 1.0 (0x3F800000), then N entries of {nameOffset, ftexSize}
// with a null-terminated name elsewhere, then a data region of, per entry, the
// .ftex bytes followed by a PSUB block holding the .ftexs sub-textures. Names
// use the GzsTool "@" (same dir as previous) / "/dir/name" scheme. Read-only.
using System.Buffers.Binary;
using System.Text;

namespace MgsvModBldr.Tools.Pftxs.Gz;

public sealed class GzPftxsEntry
{
    public string Path = "";                       // e.g. "Assets/.../foo.ftex" or "foo.1.ftexs"
    public byte[] Data = System.Array.Empty<byte>();
}

public sealed class GzPftxsFile
{
    private const uint PftxMagic = 0x58544650;     // "PFTX"
    private const uint Float1    = 0x3F800000;      // 1.0f — distinguishes GZ from TPP pftxs

    public List<GzPftxsEntry> Files { get; } = new(); // flat .ftex + .ftexs list

    // GZ pftxs = "PFTX" + the float 1.0 at offset 4 (TPP has 0x40000000 there).
    public static bool IsGzPftxs(ReadOnlySpan<byte> head)
        => head.Length >= 8
        && BinaryPrimitives.ReadUInt32LittleEndian(head) == PftxMagic
        && BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(4, 4)) == Float1;

    public static GzPftxsFile Read(Stream input)
    {
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length).ToArray());
    }

    private static GzPftxsFile Parse(byte[] b)
    {
        var f = new GzPftxsFile();

        // Header (20B): magic | float1 | size | fileCount | dataOffset.
        int fileCount  = I32(b, 12);
        int dataOffset = I32(b, 16);

        // Entry index: fileCount × { fileNameOffset, ftexSize }; name is at the
        // offset (null-terminated) in the name region before dataOffset.
        var ftexSize = new int[fileCount];
        var names    = new string[fileCount];
        int p = 20;
        for (int i = 0; i < fileCount; i++)
        {
            int nameOffset = I32(b, p);
            ftexSize[i]    = I32(b, p + 4);
            p += 8;
            names[i] = NullTermAt(b, nameOffset);
        }

        // Data region: per entry, the .ftex bytes then a PSUB block of .ftexs.
        int pos = dataOffset;
        string dir = "";
        for (int i = 0; i < fileCount; i++)
        {
            byte[] ftex = Sub(b, pos, ftexSize[i]);
            pos += ftexSize[i];

            string fn = names[i];
            string nameNoExt;
            if (fn.StartsWith("@"))                 // same directory as the previous entry
                nameNoExt = fn.Substring(1);
            else if (fn.StartsWith("/"))            // "/dir/sub/name" -> dir + name
            {
                string s = fn.Substring(1);
                int slash = s.LastIndexOf('/');
                dir = slash >= 0 ? s.Substring(0, slash) : "";
                nameNoExt = slash >= 0 ? s.Substring(slash + 1) : s;
            }
            else nameNoExt = fn;

            f.Files.Add(new GzPftxsEntry { Path = Combine(dir, nameNoExt + ".ftex"), Data = ftex });

            // PSUB: magic | count | count×{offset,size} | align16 | data blocks (align16).
            int psubCount = I32(b, pos + 4);
            int q = pos + 8;
            var subSize = new int[psubCount];
            for (int k = 0; k < psubCount; k++) { subSize[k] = I32(b, q + 4); q += 8; }
            q = Align16(q);
            for (int k = 0; k < psubCount; k++)
            {
                byte[] sub = Sub(b, q, subSize[k]);
                q += subSize[k];
                q = Align16(q);
                f.Files.Add(new GzPftxsEntry
                {
                    Path = Combine(dir, $"{nameNoExt}.{k + 1}.ftexs"),
                    Data = sub,
                });
            }
            pos = q;
        }
        return f;
    }

    private static int I32(byte[] b, int off) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(off, 4));

    private static int Align16(int v) => (v + 15) & ~15;

    private static byte[] Sub(byte[] b, int off, int len)
    {
        if (len <= 0 || off < 0 || off + len > b.Length) return System.Array.Empty<byte>();
        var r = new byte[len];
        System.Array.Copy(b, off, r, 0, len);
        return r;
    }

    private static string NullTermAt(byte[] b, int off)
    {
        int end = off;
        while (end < b.Length && b[end] != 0) end++;
        return Encoding.Latin1.GetString(b, off, end - off);
    }

    private static string Combine(string dir, string file)
        => string.IsNullOrEmpty(dir) ? file : dir + "/" + file;
}
