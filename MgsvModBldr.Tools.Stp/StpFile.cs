// .stp/.sab file model + IO
using System.Buffers.Binary;

namespace MgsvModBldr.Tools.Stp;

public enum StpVersion { GZ = 0, TPP = 1 }

public sealed class StpEntry
{
    public uint Name { get; set; }
    public byte[] Wem { get; set; } = Array.Empty<byte>();
    public byte[] Ls2 { get; set; } = Array.Empty<byte>(); // TPP only; may be empty
}

public sealed class StreamedPackage
{
    public const uint MagicLe = 0x4C505453; // 'STPL'
    public const uint MagicBe = 0x42505453; // 'STPB' (unsupported)

    public StpVersion Version { get; set; } = StpVersion.TPP;
    public List<StpEntry> Entries { get; } = new();

    public void ReadFrom(string path) { using var fs = File.OpenRead(path); Read(fs); }

    public void Read(Stream input)
    {
        long len = input.Length;
        Span<byte> head = stackalloc byte[8];
        ReadExact(input, head);
        uint sig = BinaryPrimitives.ReadUInt32LittleEndian(head);
        if (sig == MagicBe) throw new NotSupportedException("Big-endian .stp ('STPB') is not supported.");
        if (sig != MagicLe) throw new InvalidDataException("Not an .stp file (missing 'STPL').");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(4, 2));
        Version = (StpVersion)head[6];
        if (Version != StpVersion.GZ && Version != StpVersion.TPP)
            throw new InvalidDataException($"Unknown .stp version {head[6]}.");
        // head[7] = padding (reference reads+discards; always writes 0)

        var names = new uint[count];
        var wemOff = new int[count];
        var ls2Off = new int[count];
        int entrySize = Version == StpVersion.TPP ? 12 : 8;
        Span<byte> e = stackalloc byte[12];
        for (int i = 0; i < count; i++)
        {
            ReadExact(input, e.Slice(0, entrySize));
            names[i]  = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(0, 4));
            wemOff[i] = BinaryPrimitives.ReadInt32LittleEndian(e.Slice(4, 4));
            if (Version == StpVersion.TPP)
                ls2Off[i] = BinaryPrimitives.ReadInt32LittleEndian(e.Slice(8, 4));
        }

        for (int i = 0; i < count; i++)
        {
            var entry = new StpEntry { Name = names[i] };
            if (Version == StpVersion.GZ)
            {
                int wemSize = (i < count - 1 ? wemOff[i + 1] : (int)len) - wemOff[i];
                entry.Wem = ReadAt(input, wemOff[i], wemSize);
            }
            else
            {
                int ls2Size = wemOff[i] - ls2Off[i];
                int wemSize = (i < count - 1 ? ls2Off[i + 1] : (int)len) - wemOff[i];
                entry.Ls2 = ReadAt(input, ls2Off[i], ls2Size);
                entry.Wem = ReadAt(input, wemOff[i], wemSize);
            }
            Entries.Add(entry);
        }
    }

    public void Write(Stream output)
    {
        int count = Entries.Count;
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(head, MagicLe);
        BinaryPrimitives.WriteUInt16LittleEndian(head.Slice(4, 2), (ushort)count);
        head[6] = (byte)Version;
        head[7] = 0; // reference always writes 0 here
        output.Write(head);

        int entrySize = Version == StpVersion.TPP ? 12 : 8;
        long tableStart = output.Position;
        // reserve the entry table, fill offsets after laying out the data
        output.Write(new byte[(long)count * entrySize]);
        AlignWrite(output, 16);

        var wemOff = new int[count];
        var ls2Off = new int[count];
        for (int i = 0; i < count; i++)
        {
            if (Version == StpVersion.GZ)
            {
                wemOff[i] = (int)output.Position;
                output.Write(Entries[i].Wem);
            }
            else
            {
                ls2Off[i] = (int)output.Position;
                if (Entries[i].Ls2.Length > 0) output.Write(Entries[i].Ls2);
                wemOff[i] = (int)output.Position;
                output.Write(Entries[i].Wem);
            }
        }
        long end = output.Position;

        output.Position = tableStart;
        Span<byte> e = stackalloc byte[12];
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(0, 4), Entries[i].Name);
            BinaryPrimitives.WriteInt32LittleEndian(e.Slice(4, 4), wemOff[i]);
            if (Version == StpVersion.TPP)
                BinaryPrimitives.WriteInt32LittleEndian(e.Slice(8, 4), ls2Off[i]);
            output.Write(e.Slice(0, entrySize));
        }
        output.Position = end;
    }

    internal static byte[] ReadAt(Stream s, int offset, int size)
    {
        s.Position = offset;
        var b = new byte[size];
        ReadExact(s, b);
        return b;
    }

    internal static void AlignWrite(Stream s, int alignment)
    {
        int rem = (int)(s.Position % alignment);
        if (rem != 0) s.Write(new byte[alignment - rem]);
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

public sealed class SabEntry
{
    public ulong Name { get; set; }
    public byte[] Lsst { get; set; } = Array.Empty<byte>();
}

public sealed class StreamedAnimation
{
    public const uint MagicLe = 0x334C4153; // 'SAL3'
    public const uint MagicBe = 0x33424153;

    public List<SabEntry> Entries { get; } = new();

    public void ReadFrom(string path) { using var fs = File.OpenRead(path); Read(fs); }

    public void Read(Stream input)
    {
        long len = input.Length;
        Span<byte> head = stackalloc byte[8];
        StreamedPackage.ReadExact(input, head);
        uint sig = BinaryPrimitives.ReadUInt32LittleEndian(head);
        if (sig == MagicBe) throw new NotSupportedException("Big-endian .sab is not supported.");
        if (sig != MagicLe) throw new InvalidDataException("Not an .sab file (missing 'SAL3').");
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.Slice(4, 4));

        var names = new ulong[count];
        var off = new int[count];
        Span<byte> e = stackalloc byte[16];
        for (int i = 0; i < count; i++)
        {
            StreamedPackage.ReadExact(input, e);
            names[i] = BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(0, 8));
            off[i]   = BinaryPrimitives.ReadInt32LittleEndian(e.Slice(8, 4));
            // e[12..16] = zero padding
        }

        for (int i = 0; i < count; i++)
        {
            int size = (i < count - 1 ? off[i + 1] : (int)len) - off[i];
            Entries.Add(new SabEntry { Name = names[i], Lsst = StreamedPackage.ReadAt(input, off[i], size) });
        }
    }

    public void Write(Stream output)
    {
        int count = Entries.Count;
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(head, MagicLe);
        BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(4, 4), (uint)count);
        output.Write(head);

        long tableStart = output.Position;
        output.Write(new byte[(long)count * 16]);

        var off = new int[count];
        for (int i = 0; i < count; i++)
        {
            off[i] = (int)output.Position;
            output.Write(Entries[i].Lsst);
            StreamedPackage.AlignWrite(output, 16);
        }
        long end = output.Position;

        output.Position = tableStart;
        Span<byte> e = stackalloc byte[16];
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(e.Slice(0, 8), Entries[i].Name);
            BinaryPrimitives.WriteInt32LittleEndian(e.Slice(8, 4), off[i]);
            output.Write(e);
        }
        output.Position = end;
    }
}
