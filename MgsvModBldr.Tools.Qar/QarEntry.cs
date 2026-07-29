// Based on datfpk qar/qarEntry.go
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using MgsvModBldr.Tools.GameHashing;

namespace MgsvModBldr.Tools.Qar;

public sealed class QarEntryHeader
{
    public ulong  PathHash         { get; set; }
    public uint   UncompressedSize { get; set; }
    public uint   CompressedSize   { get; set; }
    public byte[] Md5Sum           { get; set; } = new byte[16];

    public string FilePath  { get; set; } = string.Empty;
    public long   DataOffset { get; set; }
    public bool   Compressed { get; set; }
    public uint   Version    { get; set; }
    public bool   MetaFlag   { get; set; }

    public ulong NameHashForPacking { get; set; }

    public void Read(Stream reader, uint version)
    {
        Version = version;
        Span<byte> buf = stackalloc byte[QarConstants.HeaderSize];
        int n = 0;
        while (n < buf.Length)
        {
            int r = reader.Read(buf.Slice(n));
            if (r == 0) throw new EndOfStreamException();
            n += r;
        }

        uint hashLow  = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice( 0, 4)) ^ QarConstants.XorMask1;
        uint hashHigh = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice( 4, 4)) ^ QarConstants.XorMask1;
        PathHash = (ulong)hashHigh << 32 | hashLow;

        uint size1 = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice( 8, 4)) ^ QarConstants.XorMask2;
        uint size2 = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(12, 4)) ^ QarConstants.XorMask3;
        CompressedSize   = size1;
        UncompressedSize = size2;
        Compressed = CompressedSize != UncompressedSize;

        Md5Sum = Md5Decode.Decode(buf.Slice(16, 16));

        DataOffset = reader.Position;
        MetaFlag = (PathHash & GameHash.PATH_CODE_USER_FLAG_MASK) > 0;
    }

    public byte[] Bytes()
    {
        if (NameHashForPacking == 0)
            PathHash = GameHash.PathCode(FilePath);
        else
            PathHash = NameHashForPacking;

        var buf = new byte[QarConstants.HeaderSize];
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan( 0, 8), PathHash ^ QarConstants.XorMask1Long);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan( 8, 4), CompressedSize   ^ QarConstants.XorMask2);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), UncompressedSize ^ QarConstants.XorMask3);
        var md5 = Md5Decode.Decode(Md5Sum);
        Buffer.BlockCopy(md5, 0, buf, 16, 16);

        DataOffset = QarConstants.HeaderSize;
        return buf;
    }
}

public sealed class QarDataHeader
{
    public uint EncryptionMagic  { get; set; }
    public uint Key              { get; set; }
    public uint CompressedSize   { get; set; }
    public uint UncompressedSize { get; set; }

    public void Parse(ReadOnlySpan<byte> data)
    {
        EncryptionMagic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (EncryptionMagic == QarConstants.EncryptionMagic1 || EncryptionMagic == QarConstants.EncryptionMagic2)
            Key = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4));
        else
            EncryptionMagic = 0;
    }

    public byte[]? Bytes()
    {
        int size = QarConstants.GetDataHeaderSize(EncryptionMagic);
        if (size == 0) return null;
        var buf = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), EncryptionMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), Key);
        if (size > 8)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan( 8, 4), UncompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12, 4), CompressedSize);
        }
        return buf;
    }
}

public sealed class QarEntry
{
    public QarEntryHeader Header     { get; } = new();
    public QarDataHeader  DataHeader { get; } = new();
    public byte[]         Data       { get; set; } = Array.Empty<byte>();

    public bool           Loaded     { get; set; }

    public void Read(Stream reader, uint version)
    {
        Header.Read(reader, version);

        var d1 = new Decrypt1Stream();
        d1.Init(Header.Md5Sum, Header.PathHash, version, 8);
        var dh = d1.Read(reader, 8);
        DataHeader.Parse(dh);
    }

    public void ReadData(Stream reader)
    {
        Loaded = true;
        reader.Position = Header.DataOffset;
        int size = (int)Math.Max(Header.UncompressedSize, Header.CompressedSize);

        var d1 = new Decrypt1Stream();
        d1.Init(Header.Md5Sum, Header.PathHash, Header.Version, size);
        Data = d1.Read(reader, size);

        if (DataHeader.EncryptionMagic > 0)
        {
            int dhSize = QarConstants.GetDataHeaderSize(DataHeader.EncryptionMagic);
            reader.Seek(dhSize, SeekOrigin.Current);
            size -= dhSize;

            var d2 = new Decrypt2Stream();
            d2.Init(DataHeader.Key);
            using var ms = new MemoryStream(Data, dhSize, size);
            Data = d2.Read(ms, size);
        }

        if (Header.Compressed)
        {
            using var src = new MemoryStream(Data);
            using var zl  = new ZLibStream(src, CompressionMode.Decompress);
            using var dst = new MemoryStream();
            zl.CopyTo(dst);
            var full = dst.ToArray();
            if (full.Length > Header.UncompressedSize)
                Array.Resize(ref full, (int)Header.UncompressedSize);
            Data = full;
        }
    }

    public byte[] Write()
    {
        Header.CompressedSize   = (uint)Data.Length;
        Header.UncompressedSize = (uint)Data.Length;

        var entryData = Data;

        if (Header.Compressed)
        {
            using var ms = new MemoryStream();
            using (var zl = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                zl.Write(entryData, 0, entryData.Length);
            Header.UncompressedSize = (uint)Data.Length;
            entryData = ms.ToArray();
            Header.CompressedSize = (uint)entryData.Length;
        }

        if (DataHeader.Key > 0)
        {
            DataHeader.EncryptionMagic  = QarConstants.EncryptionMagic2;
            DataHeader.CompressedSize   = Header.CompressedSize;
            DataHeader.UncompressedSize = Header.UncompressedSize;
            int hs = QarConstants.GetDataHeaderSize(DataHeader.EncryptionMagic);
            Header.UncompressedSize += (uint)hs;
            Header.CompressedSize   += (uint)hs;

            var d2 = new Decrypt2Stream();
            d2.Init(DataHeader.Key);
            using var ms = new MemoryStream(entryData);
            entryData = d2.Read(ms, entryData.Length);
        }

        var dataHeader = DataHeader.Bytes() ?? Array.Empty<byte>();
        var mdata = new byte[dataHeader.Length + entryData.Length];
        Buffer.BlockCopy(dataHeader, 0, mdata, 0,                 dataHeader.Length);
        Buffer.BlockCopy(entryData,  0, mdata, dataHeader.Length, entryData.Length);
        Header.Md5Sum = MD5.HashData(mdata);

        var headerBytes = Header.Bytes();

        var d1 = new Decrypt1Stream();
        d1.Init(Header.Md5Sum, Header.PathHash, Header.Version, mdata.Length);
        byte[] cipheredData;
        using (var ms = new MemoryStream(mdata))
            cipheredData = d1.Read(ms, mdata.Length);

        var result = new byte[headerBytes.Length + cipheredData.Length];
        Buffer.BlockCopy(headerBytes,  0, result, 0,                  headerBytes.Length);
        Buffer.BlockCopy(cipheredData, 0, result, headerBytes.Length, cipheredData.Length);
        return result;
    }
}
