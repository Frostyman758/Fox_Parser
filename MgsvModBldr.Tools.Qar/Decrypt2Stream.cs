// Based on datfpk qar/decrypt2stream.go
using System.Buffers.Binary;

namespace MgsvModBldr.Tools.Qar;

internal sealed class Decrypt2Stream
{
    public uint Key      { get; private set; }
    public uint BlockKey { get; private set; }

    public void Init(uint key)
    {
        Key      = key * 278u;
        BlockKey = key | ((key ^ 25974u) << 16);
    }

    public byte[] Read(Stream reader, int count)
    {
        var buf = new byte[count];
        int totalRead = 0;
        while (totalRead < buf.Length)
        {
            int n = reader.Read(buf, totalRead, buf.Length - totalRead);
            if (n == 0) break;
            totalRead += n;
        }
        if (totalRead == 0) throw new InvalidDataException("Decrypt2Stream: nothing to read");
        return Decrypt2(buf, totalRead);
    }

    public byte[] Decrypt2(byte[] input, int size)
    {
        var output = new byte[input.Length];
        Array.Copy(input, output, input.Length);

        int src = 0, dst = 0;

        while (size >= 64)
        {
            for (int j = 16; j > 0; j--)
            {
                uint x = BlockKey ^ BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(src, 4));
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dst, 4), x);
                BlockKey = Key + 48828125u * BlockKey;
                src += 4;
                dst += 4;
            }
            size -= 64;
        }

        while (size >= 16)
        {
            uint x  = BlockKey ^ BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(src,      4));
            uint v7 = Key + 48828125u * BlockKey;
            uint d2 = v7       ^ BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(src +  4, 4));
            uint v8 = Key + 48828125u * v7;
            uint d3 = v8       ^ BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(src +  8, 4));
            uint v9 = Key + 48828125u * v8;
            uint d4 = v9       ^ BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(src + 12, 4));

            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dst,      4), x);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dst +  4, 4), d2);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dst +  8, 4), d3);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dst + 12, 4), d4);

            BlockKey = Key + 48828125u * v9;
            size -= 16;
            src += 16;
            dst += 16;
        }

        while (size >= 4)
        {
            uint x = BlockKey ^ BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(src, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dst, 4), x);
            BlockKey = Key + 48828125u * BlockKey;
            size -= 4;
            src += 4;
            dst += 4;
        }

        return output;
    }
}
