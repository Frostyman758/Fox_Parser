// Based on datfpk qar/decrypt1stream.go
using System.Buffers.Binary;

namespace MgsvModBldr.Tools.Qar;

internal sealed class Decrypt1Stream
{
    public int    Size     { get; private set; }
    public int    Position { get; private set; }
    public int    Version  { get; private set; }
    public uint   HashLow  { get; private set; }
    public ulong  Seed     { get; private set; }
    public uint   SeedLow  { get; private set; }
    public uint   SeedHigh { get; private set; }

    public void Init(ReadOnlySpan<byte> md5sum, ulong pathHash, uint version, int size)
    {
        uint hashLow = (uint)(pathHash & 0xFFFFFFFFu);
        HashLow = hashLow;
        int md5Offset = (int)(hashLow % 2) * 8;
        Seed     = BinaryPrimitives.ReadUInt64LittleEndian(md5sum.Slice(md5Offset, 8));
        SeedLow  = (uint)(Seed & 0xFFFFFFFFu);
        SeedHigh = (uint)(Seed >> 32);
        Version  = (int)version;
        Size     = size;
        Position = 0;
    }

    public byte[] Read(Stream reader, int count)
    {
        if (count > (Size - Position)) count = Size - Position;

        int pad = 8 - count % 8;
        var buf = new byte[count + pad];

        int totalRead = 0;
        while (totalRead < buf.Length)
        {
            int n = reader.Read(buf, totalRead, buf.Length - totalRead);
            if (n == 0) break;
            totalRead += n;
        }
        if (totalRead == 0) return Array.Empty<byte>();

        Decrypt1(buf);
        Position += totalRead;
        if (totalRead < count) count = totalRead;
        var trimmed = new byte[count];
        Array.Copy(buf, trimmed, count);
        return trimmed;
    }

    public void Decrypt1(byte[] data)
    {
        int blocks = data.Length / 8;

        if (Version == 2)
        {
            for (int i = 0; i < blocks; i++)
            {
                int off1 = i * 8;
                int off2 = i * 8 + 4;
                int off1Abs = off1 + Position;

                int index = 2 * (int)(((ulong)HashLow + Seed + (ulong)(off1Abs / 11)) % 4);
                uint u1 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off1, 4))
                          ^ QarConstants.DecryptionTable[index] ^ SeedLow;
                uint u2 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off2, 4))
                          ^ QarConstants.DecryptionTable[index + 1] ^ SeedHigh;
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off1, 4), u1);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off2, 4), u2);
            }

            int rem = data.Length % 8;
            for (int i = 0; i < rem; i++)
            {
                int offset      = blocks * 8 + i;
                int offsetBlock = offset - offset % 8;
                int offBlockAbs = offsetBlock + Position;

                int index = 2 * (int)(((ulong)HashLow + Seed + (ulong)(offBlockAbs / 11)) % 4);
                int decIndex = offset % 8;

                uint xorMask  = QarConstants.DecryptionTable[index + 1];
                uint seedMask = SeedHigh;
                if (decIndex < 4)
                {
                    xorMask  = QarConstants.DecryptionTable[index];
                    seedMask = SeedLow;
                }

                byte xorByte  = (byte)((xorMask  >> (8 * (decIndex % 4))) & 0xFF);
                byte seedByte = (byte)((seedMask >> (8 * (decIndex % 4))) & 0xFF);
                data[offset] = (byte)(data[offset] ^ (xorByte ^ seedByte));
            }
            return;
        }

        for (int i = 0; i < blocks; i++)
        {
            int off1 = i * 8;
            int off2 = i * 8 + 4;
            int off1Abs = off1 + Position;

            int index = 2 * (int)(((ulong)HashLow + (ulong)(off1Abs / 11)) % 4);
            uint u1 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off1, 4))
                      ^ QarConstants.DecryptionTable[index];
            uint u2 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off2, 4))
                      ^ QarConstants.DecryptionTable[index + 1];
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off1, 4), u1);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off2, 4), u2);
        }

        int rem1 = data.Length % 8;
        for (int i = 0; i < rem1; i++)
        {
            int offset = blocks * 8 + i;
            int offsetAbs = offset + Position;
            int index = 2 * (int)((HashLow + (uint)(offsetAbs - offsetAbs % 8)) / 11 % 4);
            int decIndex = offset % 8;
            uint xorMask = QarConstants.DecryptionTable[index + 1];
            if (decIndex < 4) xorMask = QarConstants.DecryptionTable[index];
            byte xorByte = (byte)((xorMask >> (8 * decIndex)) & 0xFF);
            data[offset] = (byte)(data[offset] ^ xorByte);
        }
    }
}
