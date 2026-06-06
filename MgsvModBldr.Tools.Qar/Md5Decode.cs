// Based on datfpk qar/md5.go
using System.Buffers.Binary;

namespace MgsvModBldr.Tools.Qar;

internal static class Md5Decode
{
    public static byte[] Decode(ReadOnlySpan<byte> input)
    {
        if (input.Length != 16) throw new InvalidDataException($"MD5 must be 16 bytes (got {input.Length})");

        uint w0 = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice( 0, 4)) ^ QarConstants.XorMask4;
        uint w1 = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice( 4, 4)) ^ QarConstants.XorMask1;
        uint w2 = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice( 8, 4)) ^ QarConstants.XorMask1;
        uint w3 = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(12, 4)) ^ QarConstants.XorMask2;

        var output = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan( 0, 4), w0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan( 4, 4), w1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan( 8, 4), w2);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12, 4), w3);
        return output;
    }
}
