using System.Buffers.Binary;
using System.Text;

namespace MgsvModBldr.Tools.Sbp;

/// <summary>
/// One sub-file inside an .sbp. <see cref="Magic"/> is the trimmed 4-byte
/// tag ("bnk", "stp", "sab"); <see cref="Offset"/>/<see cref="Size"/> are
/// recomputed on write so only the magic + payload matter for round-trip.
/// </summary>
public sealed class SbpEntry
{
    public const int HeaderSize = 12;

    /// <summary>4-byte file tag, trailing NULs trimmed (e.g. "bnk").</summary>
    public string Magic { get; set; } = "";
    public uint Offset { get; set; }
    public int Size { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Sound Bank Package (.sbp) container. Format (little-endian):
///   uint32 'SBPL' | byte fileCount | uint16 headerSize | byte padding(0)
///   fileCount × { char[4] magic | uint32 offset | int32 size }
///   data blocks, each on a 16-byte boundary (0x00 padding).
/// headerSize == 8 + fileCount*12. Lossless: unpack→repack is byte-exact.
///
/// Functionally identical to GzsTool's Sbp handling, but the raw 4-byte
/// magic is preserved (GzsTool re-derives it from the file extension);
/// preserving it keeps the round-trip exact regardless of the tag.
/// </summary>
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

        for (int i = 0; i < fileCount; i++)
        {
            Span<byte> e = stackalloc byte[SbpEntry.HeaderSize];
            ReadExact(input, e);
            Entries.Add(new SbpEntry
            {
                Magic  = Encoding.ASCII.GetString(e.Slice(0, 4)).TrimEnd('\0'),
                Offset = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(4, 4)),
                Size   = BinaryPrimitives.ReadInt32LittleEndian(e.Slice(8, 4)),
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

        foreach (var e in Entries)
        {
            Span<byte> eb = stackalloc byte[SbpEntry.HeaderSize];
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
