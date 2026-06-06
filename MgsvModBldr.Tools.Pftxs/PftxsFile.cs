// Based on GzsTool.Core/Pftxs/PftxsFile.cs, PftxsFtexFile.cs, PftxsFtexsFileEntry.cs
using System.Buffers.Binary;

namespace MgsvModBldr.Tools.Pftxs;

public sealed class PftxsEntry
{
    public const int HeaderSize = 16;
    public ulong  Hash   { get; set; }
    public int    Offset { get; set; }
    public int    Size   { get; set; }
    public byte[] Data   { get; set; } = Array.Empty<byte>();
    public string FilePath { get; set; } = string.Empty;
    public bool   Resolved { get; set; }

    public void ReadHeader(Stream r)
    {
        Span<byte> b = stackalloc byte[HeaderSize];
        ReadExact(r, b);
        Hash   = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(0, 8));
        Offset = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(8, 4));
        Size   = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(12, 4));
    }

    public void WriteHeader(Stream w)
    {
        Span<byte> b = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt64LittleEndian(b.Slice(0, 8), Hash);
        BinaryPrimitives.WriteInt32LittleEndian(b.Slice(8, 4), Offset);
        BinaryPrimitives.WriteInt32LittleEndian(b.Slice(12, 4), Size);
        w.Write(b);
    }

    internal static void ReadExact(Stream s, Span<byte> buf)
    {
        int n = 0;
        while (n < buf.Length)
        {
            int r = s.Read(buf.Slice(n));
            if (r == 0) throw new EndOfStreamException();
            n += r;
        }
    }
}

public sealed class PftxsGroup
{
    public const int HeaderSize = 32;
    public ulong Hash { get; set; }
    public List<PftxsEntry> Entries { get; } = new();

    public void Read(Stream r)
    {
        long basePos = r.Position;
        Span<byte> hdr = stackalloc byte[HeaderSize];
        PftxsEntry.ReadExact(r, hdr);
        // hdr[0..4] = "FTEX"
        Hash = BinaryPrimitives.ReadUInt64LittleEndian(hdr.Slice(8, 8));
        int count = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(16, 4));

        for (int i = 0; i < count; i++)
        {
            var e = new PftxsEntry();
            e.ReadHeader(r);
            Entries.Add(e);
        }

        foreach (var e in Entries)
        {
            r.Position = basePos + e.Offset;
            var d = new byte[e.Size];
            PftxsEntry.ReadExact(r, d);
            e.Data = d;
        }
    }

    public void Write(Stream w)
    {
        long groupPos = w.Position;
        w.Position += HeaderSize + (long)Entries.Count * PftxsEntry.HeaderSize;

        foreach (var e in Entries)
        {
            e.Offset = (int)(w.Position - groupPos);
            e.Size   = e.Data.Length;
            w.Write(e.Data, 0, e.Data.Length);
        }

        long end = w.Position;
        w.Position = groupPos;
        Span<byte> hdr = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(0, 4), 0x58455446u);     // FTEX
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), (uint)(end - groupPos));
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(8, 8), Hash);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(16, 4), (uint)Entries.Count);
        w.Write(hdr);
        foreach (var e in Entries) e.WriteHeader(w);
        w.Position = end;
    }
}

public sealed class PftxsFile
{
    public List<PftxsGroup> Groups { get; } = new();

    private const int PftxHeaderSize = 16;
    private const int TexlHeaderSize = 16;

    public void ReadFrom(string path)
    {
        using var fs = File.OpenRead(path);
        Read(fs);
    }

    public void Read(Stream r)
    {
        Span<byte> hdr = stackalloc byte[32];
        PftxsEntry.ReadExact(r, hdr);
        // hdr[0..4]="PFTX", hdr[16..20]="TEXL"
        int fileCount = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(24, 4));

        for (int i = 0; i < fileCount; i++)
        {
            var g = new PftxsGroup();
            g.Read(r);
            Groups.Add(g);
        }
    }

    public void Write(Stream w)
    {
        w.Position = PftxHeaderSize + TexlHeaderSize;
        foreach (var g in Groups) g.Write(w);

        long end = w.Position;
        w.Position = 0;
        Span<byte> pftx = stackalloc byte[PftxHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(pftx.Slice(0, 4),  0x58544650u);    // PFTX
        BinaryPrimitives.WriteUInt32LittleEndian(pftx.Slice(4, 4),  0x40000000u);
        BinaryPrimitives.WriteUInt32LittleEndian(pftx.Slice(8, 4),  0x00000010u);
        BinaryPrimitives.WriteUInt32LittleEndian(pftx.Slice(12, 4), 0x00000001u);
        w.Write(pftx);

        Span<byte> texl = stackalloc byte[TexlHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(texl.Slice(0, 4),  0x4C584554u);    // TEXL
        BinaryPrimitives.WriteUInt32LittleEndian(texl.Slice(4, 4),  (uint)(end - PftxHeaderSize));
        BinaryPrimitives.WriteUInt32LittleEndian(texl.Slice(8, 4),  (uint)Groups.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(texl.Slice(12, 4), 0u);
        w.Write(texl);
        w.Position = end;
    }
}
