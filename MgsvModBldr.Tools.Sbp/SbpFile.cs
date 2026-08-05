// .sbp file model + IO
using System.Buffers.Binary;
using System.Text;

namespace MgsvModBldr.Tools.Sbp;

public sealed class SbpEntry
{
    public const int HeaderSize = 12;

    public string Magic { get; set; } = "";
    public uint Offset { get; set; }
    public int Size { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

public sealed class SbpFile
{
    public const uint MagicLe = 0x4C504253; // 'SBPL'
    public const int FileHeaderSize = 8;

    public List<SbpEntry> Entries { get; } = new();

    public void ReadFrom(string path)
    {
        using var fs = File.OpenRead(path);
        Read(fs);
    }

    public void Read(Stream input)
    {
        Span<byte> head = stackalloc byte[FileHeaderSize];
        ReadExact(input, head);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
        if (magic != MagicLe)
            throw new InvalidDataException("Not an SBP file (missing 'SBPL' magic).");
        int fileCount = head[4];
        // head[5..7] = headerSize (recomputed on write), head[7] = padding.

        Span<byte> entryBuf = stackalloc byte[SbpEntry.HeaderSize];
        for (int i = 0; i < fileCount; i++)
        {
            ReadExact(input, entryBuf);
            Entries.Add(new SbpEntry
            {
                Magic  = Encoding.ASCII.GetString(entryBuf.Slice(0, 4)).TrimEnd('\0'),
                Offset = BinaryPrimitives.ReadUInt32LittleEndian(entryBuf.Slice(4, 4)),
                Size   = BinaryPrimitives.ReadInt32LittleEndian(entryBuf.Slice(8, 4)),
            });
        }

        foreach (var e in Entries)
        {
            input.Position = e.Offset;
            var data = new byte[e.Size];
            ReadExact(input, data);
            e.Data = data;
        }
    }

    public void Write(Stream output)
    {
        if (Entries.Count > byte.MaxValue)
            throw new InvalidDataException($"SBP supports at most 255 entries (got {Entries.Count}).");

        int headerSize = FileHeaderSize + Entries.Count * SbpEntry.HeaderSize;
        long start = output.Position;

        // Reserve the header, then lay out the 16-aligned data blocks first
        // so we know each entry's real offset before writing the table.
        output.Position = start + headerSize;
        AlignWrite(output, 16);
        foreach (var e in Entries)
        {
            e.Offset = (uint)output.Position;
            e.Size   = e.Data.Length;
            output.Write(e.Data, 0, e.Data.Length);
            AlignWrite(output, 16);
        }
        long end = output.Position;

        // Header + entry table.
        output.Position = start;
        Span<byte> head = stackalloc byte[FileHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(head, MagicLe);
        head[4] = (byte)Entries.Count;
        BinaryPrimitives.WriteUInt16LittleEndian(head.Slice(5, 2), (ushort)headerSize);
        head[7] = 0;
        output.Write(head);

        Span<byte> eb = stackalloc byte[SbpEntry.HeaderSize];
        foreach (var e in Entries)
        {
            eb.Clear(); // magic field NUL-padded to 4 bytes
            var mb = Encoding.ASCII.GetBytes(e.Magic);
            mb.AsSpan(0, Math.Min(4, mb.Length)).CopyTo(eb.Slice(0, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(eb.Slice(4, 4), e.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(eb.Slice(8, 4), e.Size);
            output.Write(eb);
        }

        output.Position = end;
    }

    private static void AlignWrite(Stream s, int alignment)
    {
        int rem = (int)(s.Position % alignment);
        if (rem == 0) return;
        Span<byte> pad = stackalloc byte[16];
        pad.Clear();
        s.Write(pad.Slice(0, alignment - rem));
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
