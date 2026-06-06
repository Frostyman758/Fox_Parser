// Based on datfpk fpk/entry.go Decrypt/Encrypt
using System.Buffers.Binary;
using System.Text;
using MgsvModBldr.Tools.GameHashing;

namespace MgsvModBldr.Tools.Fpk;

internal static class FpkCrypto
{
    private static ulong KeyHash(string entryName)
    {
        var lower = entryName.ToLowerInvariant();
        int slash = lower.LastIndexOfAny(new[] { '/', '\\' });
        var baseName = slash >= 0 ? lower[(slash + 1)..] : lower;
        return GameHash.StringId(baseName);
    }

    public static bool TryDecrypt(byte[] data, string entryName, out byte[] result)
    {
        ulong h = KeyHash(entryName);
        Span<byte> key = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(key, ~h);

        var res = new byte[data.Length - 1];
        for (int i = 0; i < data.Length - 1; i++)
        {
            key[i % 8] ^= data[i + 1];
            res[i] = key[i % 8];
        }

        if (res.Length == 0 || res[^1] != 0)
        {
            result = res;
            return false;
        }
        result = res[..^1];
        return true;
    }

    public static byte[] Encrypt(byte[] data, string entryName)
    {
        ulong h = KeyHash(entryName);
        Span<byte> key = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(key, ~h);

        var padded = new byte[data.Length + 1];
        Buffer.BlockCopy(data, 0, padded, 0, data.Length);

        var res = new byte[padded.Length];
        for (int i = 0; i < padded.Length; i++)
        {
            res[i] = (byte)(key[i % 8] ^ padded[i]);
            key[i % 8] = padded[i];
        }

        var outBuf = new byte[res.Length + 1];
        outBuf[0] = 0x1B;
        Buffer.BlockCopy(res, 0, outBuf, 1, res.Length);
        return outBuf;
    }
}
