// Based on FoxKit Fox.Kernel/Hashing/CityHash/CityHash.cs
using System.Text;

namespace MgsvModBldr.Tools.GameHashing;

internal readonly struct UInt128
{
    public UInt128(ulong low, ulong high) { Low = low; High = high; }
    public ulong Low  { get; }
    public ulong High { get; }
}

public static class GameCityHash
{
    private const ulong K0 = 0xc3a5c85c97cb3127;
    private const ulong K1 = 0xb492b66fbe98f273;
    private const ulong K2 = 0x9ae16a3b2f90404f;
    private const ulong K3 = 0xc949d7c7509e6557;

    private static ulong Hash128To64(UInt128 x)
    {
        const ulong kMul = 0x9ddfea08eb382d69;
        ulong a = (x.Low ^ x.High) * kMul;
        a ^= a >> 47;
        ulong b = (x.High ^ a) * kMul;
        b ^= b >> 47;
        b *= kMul;
        return b;
    }

    private static ulong RotateByAtLeast1(ulong val, int shift) => (val >> shift) | (val << (64 - shift));
    private static ulong Rotate(ulong val, int shift) => shift == 0 ? val : (val >> shift) | (val << (64 - shift));
    private static ulong ShiftMix(ulong val) => val ^ (val >> 47);
    private static ulong HashLen16(ulong u, ulong v) => Hash128To64(new UInt128(u, v));

    private static ulong Fetch64(ReadOnlySpan<byte> p, int index) => BitConverter.ToUInt64(p.Slice(index));
    private static uint  Fetch32(ReadOnlySpan<byte> p, int index) => BitConverter.ToUInt32(p.Slice(index));

    private static ulong HashLen0To16(ReadOnlySpan<byte> s, int offset)
    {
        int len = s.Length - offset;
        if (len > 8)
        {
            ulong a = Fetch64(s, offset);
            ulong b = Fetch64(s, offset + len - 8);
            return HashLen16(a, RotateByAtLeast1(b + (ulong)len, len)) ^ b;
        }
        if (len >= 4)
        {
            ulong a = Fetch32(s, offset);
            return HashLen16((uint)len + (a << 3), Fetch32(s, offset + len - 4));
        }
        if (len > 0)
        {
            byte a = s[offset];
            byte b = s[offset + (len >> 1)];
            byte c = s[offset + (len - 1)];
            uint y = (uint)(a + ((uint)b << 8));
            uint z = (uint)(len + ((uint)c << 2));
            return ShiftMix(y * K2 ^ z * K3) * K2;
        }
        return K2;
    }

    private static ulong HashLen17To32(ReadOnlySpan<byte> s)
    {
        uint len = (uint)s.Length;
        ulong a = Fetch64(s, 0) * K1;
        ulong b = Fetch64(s, 8);
        ulong c = Fetch64(s, (int)(len - 8)) * K2;
        ulong d = Fetch64(s, (int)(len - 16)) * K0;
        return HashLen16(Rotate(a - b, 43) + Rotate(c, 30) + d, a + Rotate(b ^ K3, 20) - c + len);
    }

    private static UInt128 WeakHashLen32WithSeeds(ulong w, ulong x, ulong y, ulong z, ulong a, ulong b)
    {
        a += w;
        b = Rotate(b + a + z, 21);
        ulong c = a;
        a += x;
        a += y;
        b += Rotate(a, 44);
        return new UInt128(a + z, b + c);
    }

    private static UInt128 WeakHashLen32WithSeeds(ReadOnlySpan<byte> s, int offset, ulong a, ulong b)
        => WeakHashLen32WithSeeds(Fetch64(s, offset), Fetch64(s, offset + 8),
                                  Fetch64(s, offset + 16), Fetch64(s, offset + 24), a, b);

    private static ulong HashLen33To64(ReadOnlySpan<byte> s)
    {
        uint len = (uint)s.Length;
        ulong z = Fetch64(s, 24);
        ulong a = Fetch64(s, 0) + (len + Fetch64(s, (int)(len - 16))) * K0;
        ulong b = Rotate(a + z, 52);
        ulong c = Rotate(a, 37);
        a += Fetch64(s, 8);
        c += Rotate(a, 7);
        a += Fetch64(s, 16);
        ulong vf = a + z;
        ulong vs = b + Rotate(a, 31) + c;
        a = Fetch64(s, 16) + Fetch64(s, (int)(len - 32));
        z = Fetch64(s, (int)(len - 8));
        b = Rotate(a + z, 52);
        c = Rotate(a, 37);
        a += Fetch64(s, (int)(len - 24));
        c += Rotate(a, 7);
        a += Fetch64(s, (int)(len - 16));
        ulong wf = a + z;
        ulong ws = b + Rotate(a, 31) + c;
        ulong r = ShiftMix((vf + ws) * K2 + (wf + vs) * K0);
        return ShiftMix(r * K0 + vs) * K2;
    }

    private static ulong CityHash64(ReadOnlySpan<byte> s)
    {
        int len = s.Length;
        if (len <= 32) return len <= 16 ? HashLen0To16(s, 0) : HashLen17To32(s);
        if (len <= 64) return HashLen33To64(s);

        ulong x = Fetch64(s, len - 40);
        ulong y = Fetch64(s, len - 16) + Fetch64(s, len - 56);
        ulong z = HashLen16(Fetch64(s, len - 48) + (ulong)len, Fetch64(s, len - 24));
        UInt128 v = WeakHashLen32WithSeeds(s, len - 64, (ulong)len, z);
        UInt128 w = WeakHashLen32WithSeeds(s, len - 32, y + K1, x);
        x = x * K1 + Fetch64(s, 0);

        len = (s.Length - 1) & ~63;
        int offset = 0;
        do
        {
            x = Rotate(x + y + v.Low + Fetch64(s, offset + 8), 37) * K1;
            y = Rotate(y + v.High + Fetch64(s, offset + 48), 42) * K1;
            x ^= w.High;
            y += v.Low + Fetch64(s, offset + 40);
            z = Rotate(z + w.Low, 33) * K1;
            v = WeakHashLen32WithSeeds(s, offset, v.High * K1, x + w.Low);
            w = WeakHashLen32WithSeeds(s, offset + 32, z + w.High, y + Fetch64(s, offset + 16));
            (z, x) = (x, z);
            offset += 64;
            len -= 64;
        } while (len != 0);

        return HashLen16(HashLen16(v.Low, w.Low) + ShiftMix(y) * K1 + z, HashLen16(v.High, w.High) + x);
    }

    public static ulong CityHash64WithSeeds(ReadOnlySpan<char> s, ulong seed0, ulong seed1)
        => CityHash64WithSeeds(Encoding.ASCII.GetBytes(new string(s)), seed0, seed1);

    public static ulong CityHash64WithSeeds(ReadOnlySpan<byte> s, ulong seed0, ulong seed1)
        => HashLen16(CityHash64(s) - seed0, seed1);
}
