// Based on datfpk fpk/fpk.go, header.go, reference.go
using System.Buffers.Binary;

namespace MgsvModBldr.Tools.Fpk;

public sealed class FpkFile
{
    public static readonly byte[] MagicFpk  = { 0x66, 0x6f, 0x78, 0x66, 0x70, 0x6b, 0x00, 0x77, 0x69, 0x6e }; // foxfpk\0win
    public static readonly byte[] MagicFpkd = { 0x66, 0x6f, 0x78, 0x66, 0x70, 0x6b, 0x64, 0x77, 0x69, 0x6e }; // foxfpkdwin

    public const int HeaderSize = 48;

    public bool IsFpkd { get; private set; }
    public uint FileSize { get; private set; }

    public List<FpkEntry>  Entries    { get; } = new();
    public List<FpkString> References { get; } = new();

    public string FilePath { get; private set; } = string.Empty;

    public void ReadFrom(string path)
    {
        FilePath = path;
        using var fs = File.OpenRead(path);
        Read(fs);
    }

    public void Read(Stream r)
    {
        Span<byte> hdr = stackalloc byte[HeaderSize];
        FpkString.ReadExact(r, hdr);

        var magic = hdr.Slice(0, 10);
        if (magic.SequenceEqual(MagicFpkd)) IsFpkd = true;
        else if (magic.SequenceEqual(MagicFpk)) IsFpkd = false;
        else throw new InvalidDataException("unknown fpk(d) magic");

        FileSize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(10, 4));
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(36, 4));
        uint refCount   = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(40, 4));

        for (int i = 0; i < entryCount; i++)
        {
            var e = new FpkEntry();
            e.Read(r);
            Entries.Add(e);
        }
        for (int i = 0; i < refCount; i++)
        {
            var s = new FpkString();
            s.Read(r);
            References.Add(s);
        }
    }

    public void Write(Stream w, string baseDir)
    {
        long refDataPos = HeaderSize
                        + (long)Entries.Count * FpkEntry.EntrySize
                        + (long)References.Count * FpkString.HeaderSize;
        w.Seek(refDataPos, SeekOrigin.Begin);

        foreach (var e in Entries)   e.FilePath.WriteData(w);
        foreach (var rf in References) rf.WriteData(w);

        AlignWrite(w, 16);

        foreach (var e in Entries)
        {
            // Only pull from disk when the entry was never loaded — a loaded
            // entry with zero-length Data is a legitimate empty file.
            if (e.Data.Length == 0 && !e.Loaded)
            {
                var rel = e.FilePath.Data.TrimStart('/').Replace('\\', '/');
                e.Data = File.ReadAllBytes(Path.Combine(baseDir, rel));
            }
            e.WriteData(w);
            AlignWrite(w, 16);
        }

        FileSize = (uint)w.Position;

        w.Seek(0, SeekOrigin.Begin);
        Span<byte> hdr = stackalloc byte[HeaderSize];
        (IsFpkd ? MagicFpkd : MagicFpk).CopyTo(hdr);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(10, 4), FileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(32, 4), 2u);                   // magicNumber2
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(36, 4), (uint)Entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(40, 4), (uint)References.Count);
        w.Write(hdr);

        foreach (var e in Entries)    e.WriteHeader(w);
        foreach (var rf in References) rf.WriteHeader(w);
    }

    public void SetType(bool isFpkd) => IsFpkd = isFpkd;

    private static void AlignWrite(Stream s, int alignment)
    {
        long pos = s.Position;
        if (pos % alignment != 0)
        {
            int pad = (int)(alignment - pos % alignment);
            Span<byte> zeros = stackalloc byte[16];
            s.Write(zeros.Slice(0, pad));
        }
    }
}
