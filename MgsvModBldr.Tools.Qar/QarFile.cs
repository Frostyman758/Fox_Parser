// Based on datfpk qar/qar.go
using System.Buffers.Binary;
using MgsvModBldr.Tools.GameHashing;

namespace MgsvModBldr.Tools.Qar;

public sealed class QarFile
{
    public byte[] Magic           { get; private set; } = (byte[])QarConstants.Magic.Clone();
    public uint   Flags           { get; set; }
    public uint   FileCount       { get; private set; }
    public uint   UnknownCount    { get; set; }
    public uint   BlockFileEnd    { get; private set; }
    public uint   OffsetFirstFile { get; private set; }
    public uint   Version         { get; set; } = 2;
    public uint   Unknown2        { get; private set; }

    public string FilePath { get; private set; } = string.Empty;
    public List<QarEntry> Entries { get; } = new();

    private Stream? _handle;

    public void ReadFrom(string path)
    {
        FilePath = path;
        var fs = File.OpenRead(path);
        try { Read(fs); }
        catch { fs.Dispose(); throw; }
    }

    public void Close()
    {
        _handle?.Dispose();
        _handle = null;
    }

    public void Read(Stream f)
    {
        _handle = f;

        Span<byte> hdr = stackalloc byte[32];
        int n = 0;
        while (n < hdr.Length)
        {
            int r = f.Read(hdr.Slice(n));
            if (r == 0) throw new EndOfStreamException("QAR header truncated");
            n += r;
        }

        Magic = hdr.Slice(0, 4).ToArray();
        if (Magic[0] != 0x53 || Magic[1] != 0x51 || Magic[2] != 0x41 || Magic[3] != 0x52)
            throw new InvalidDataException("QAR magic mismatch");

        Flags           = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice( 4, 4)) ^ QarConstants.XorMask1;
        FileCount       = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice( 8, 4)) ^ QarConstants.XorMask2;
        UnknownCount    = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(12, 4)) ^ QarConstants.XorMask3;
        BlockFileEnd    = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(16, 4)) ^ QarConstants.XorMask4;
        OffsetFirstFile = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(20, 4)) ^ QarConstants.XorMask1;
        Version         = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(24, 4)) ^ QarConstants.XorMask1;
        Unknown2        = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(28, 4)) ^ QarConstants.XorMask2;

        int blockShiftBits = (Flags & 0x800) > 0 ? 12 : 10;

        var sectionsBlob = new byte[8 * FileCount];
        int sn = 0;
        while (sn < sectionsBlob.Length)
        {
            int r = f.Read(sectionsBlob, sn, sectionsBlob.Length - sn);
            if (r == 0) throw new EndOfStreamException("QAR section list truncated");
            sn += r;
        }
        var sections = DecryptSectionList(FileCount, sectionsBlob, Version, encrypt: false);

        foreach (var section in sections)
        {
            ulong sectionBlock  = section >> 40;
            ulong sectionOffset = sectionBlock << blockShiftBits;
            f.Seek((long)sectionOffset, SeekOrigin.Begin);

            var entry = new QarEntry();
            entry.Read(f, Version);
            Entries.Add(entry);
        }
    }

    public static ulong[] DecryptSectionList(uint fileCount, byte[] sections, uint version, bool encrypt)
    {
        var result = new ulong[fileCount];

        if (version == 2)
        {
            ulong xor = 0xA2C18EC3UL;
            for (int i = 0; i < result.Length; i++)
            {
                ulong off1 = (ulong)i * 8;
                ulong off2 = off1 + 4;
                int idx1 = (int)((xor + (off1 / 5)) % 4);
                int idx2 = (int)((xor + (off2 / 5)) % 4);

                uint s1 = BinaryPrimitives.ReadUInt32LittleEndian(sections.AsSpan((int)off1, 4));
                uint s2 = BinaryPrimitives.ReadUInt32LittleEndian(sections.AsSpan((int)off2, 4));

                uint i1 = s1 ^ QarConstants.XorTable[idx1];
                uint i2 = s2 ^ QarConstants.XorTable[idx2];
                result[i] = (ulong)i2 << 32 | i1;

                if (encrypt)
                {
                    i1 = s1;
                    i2 = s2;
                }

                int rotation = (int)(i2 / 256) % 19;
                ulong rotated = (ulong)((i1 >> rotation) | (i1 << (32 - rotation))); // ROR
                xor ^= rotated;
            }
            return result;
        }

        for (int i = 0; i < result.Length; i++)
        {
            ulong off1 = (ulong)i * 8;
            ulong off2 = off1 + 4;
            int idx1 = (int)(((ulong)i + (off1 / 5)) % 4);
            int idx2 = (int)(((ulong)i + (off2 / 5)) % 4);

            uint s1 = BinaryPrimitives.ReadUInt32LittleEndian(sections.AsSpan((int)off1, 4));
            uint s2 = BinaryPrimitives.ReadUInt32LittleEndian(sections.AsSpan((int)off2, 4));
            s1 ^= QarConstants.XorTable[idx1];
            s2 ^= QarConstants.XorTable[idx2];
            result[i] = (ulong)s2 << 32 | s1;
        }
        return result;
    }

    public byte[] EncryptSections(ulong[] sections)
    {
        var blob = new byte[sections.Length * QarConstants.BlockSize];
        for (int i = 0; i < sections.Length; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(i * 8, 8), sections[i]);

        var encSections = DecryptSectionList(FileCount, blob, Version, encrypt: true);
        for (int i = 0; i < encSections.Length; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(i * 8, 8), encSections[i]);
        return blob;
    }

    public void Write(Stream file, string baseDir)
    {
        _handle = file;
        int shift = (Flags & 0x800) > 0 ? 12 : 10;
        int alignment = 1 << shift;

        file.Seek(QarConstants.HeaderSize + QarConstants.BlockSize * Entries.Count, SeekOrigin.Begin);
        long dataOffset = AlignWrite(file, alignment);
        OffsetFirstFile = (uint)dataOffset;

        var sections = new ulong[Entries.Count];
        for (int i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            e.Header.Version = Version;

            if (e.Header.NameHashForPacking == 0)
                e.Header.PathHash = GameHash.PathCode(e.Header.FilePath);
            else
                e.Header.PathHash = e.Header.NameHashForPacking;

            long pos = file.Position;
            ulong section = (ulong)(pos >> shift) << 40
                          | (e.Header.PathHash & 0xFF) << 32
                          | (e.Header.PathHash >> 32 & 0xFFFFFFFFFFUL);
            sections[i] = section;

            if (e.Data.Length == 0 && !e.Loaded)
            {
                var rel = e.Header.FilePath.TrimStart('/').Replace('\\', '/');
                var p = Path.Combine(baseDir, rel);
                e.Data = File.ReadAllBytes(p);
            }

            var data = e.Write();
            file.Write(data, 0, data.Length);
            AlignWrite(file, alignment);
        }

        long endPos = file.Position;
        BlockFileEnd = (uint)(endPos >> shift);
        FileCount = (uint)Entries.Count;

        file.Seek(0, SeekOrigin.Begin);
        file.Write(Magic, 0, 4);
        Span<byte> hdr = stackalloc byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice( 0, 4), Flags           ^ QarConstants.XorTable[0]);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice( 4, 4), FileCount       ^ QarConstants.XorTable[1]);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice( 8, 4), UnknownCount    ^ QarConstants.XorTable[2]);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(12, 4), BlockFileEnd    ^ QarConstants.XorTable[3]);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(16, 4), OffsetFirstFile ^ QarConstants.XorTable[0]);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(20, 4), Version         ^ QarConstants.XorTable[0]);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(24, 4), 0u              ^ QarConstants.XorTable[1]);
        file.Write(hdr);

        var sectionBlob = EncryptSections(sections);
        file.Write(sectionBlob, 0, sectionBlob.Length);
    }

    private static long AlignWrite(Stream s, int alignment)
    {
        long pos = s.Position;
        if (pos % alignment != 0)
        {
            int pad = (int)(alignment - pos % alignment);
            var zeros = new byte[pad];
            s.Write(zeros, 0, pad);
        }
        return s.Position;
    }

    public byte[]? ReadFile(string path)
    {
        if (_handle is null) throw new InvalidOperationException("Archive not opened.");
        ulong ph = GameHash.PathCode(path);
        foreach (var e in Entries)
        {
            if (e.Header.PathHash == ph)
            {
                e.ReadData(_handle);
                return e.Data;
            }
        }
        return null;
    }
}
