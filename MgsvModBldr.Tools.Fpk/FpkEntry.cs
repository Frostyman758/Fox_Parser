// Based on datfpk fpk/entry.go
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MgsvModBldr.Tools.Fpk;

public sealed class FpkEntry
{
    public uint       DataOffset { get; set; }
    public uint       DataSize   { get; set; }
    public FpkString  FilePath   { get; } = new();
    public byte[]     PathMD5    { get; set; } = new byte[16];
    public byte[]     Data       { get; set; } = Array.Empty<byte>();
    public bool       Encrypted  { get; set; }

    /// <summary>
    /// True once <see cref="Data"/> holds this entry's authoritative content
    /// (set by <see cref="ReadData"/>). Distinguishes a legitimately EMPTY
    /// 0-byte file from an entry whose data still needs loading from disk —
    /// <see cref="FpkFile.Write"/> only falls back to a disk read when this is
    /// false, so empty inner files (e.g. 0-byte .fcnp) survive an in-memory
    /// merge instead of triggering a spurious "file not found".
    /// </summary>
    public bool       Loaded     { get; set; }

    public const int EntrySize = 4 * 4 + FpkString.HeaderSize + 16; // 48

    public void Read(Stream r)
    {
        Span<byte> info = stackalloc byte[16];
        FpkString.ReadExact(r, info);
        DataOffset = BinaryPrimitives.ReadUInt32LittleEndian(info.Slice(0, 4));
        DataSize   = BinaryPrimitives.ReadUInt32LittleEndian(info.Slice(8, 4));

        FilePath.Read(r);
        FpkString.ReadExact(r, PathMD5);

        ReadData(r);
    }

    public void ReadData(Stream r)
    {
        Loaded = true;
        if (DataSize < 1) { Data = Array.Empty<byte>(); return; }
        long cur = r.Position;
        r.Seek(DataOffset, SeekOrigin.Begin);
        var b = new byte[DataSize];
        FpkString.ReadExact(r, b);
        r.Seek(cur, SeekOrigin.Begin);

        if (b.Length > 0 && (b[0] == 0x1B || b[0] == 0x1C))
        {
            if (FpkCrypto.TryDecrypt(b, FilePath.Data, out var dec))
            {
                Data = dec;
                Encrypted = true;
            }
            else
            {
                Data = b;
            }
        }
        else
        {
            Data = b;
        }
    }

    public void WriteData(Stream w)
    {
        var payload = Encrypted ? FpkCrypto.Encrypt(Data, FilePath.Data) : Data;
        DataOffset = (uint)w.Position;
        DataSize   = (uint)payload.Length;
        w.Write(payload, 0, payload.Length);
    }

    public void WriteHeader(Stream w)
    {
        Span<byte> info = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(0, 4),  DataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(4, 4),  0);
        BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(8, 4),  DataSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info.Slice(12, 4), 0);
        w.Write(info);

        FilePath.WriteHeader(w);

        if (IsMd5Empty(PathMD5))
            PathMD5 = MD5.HashData(Encoding.UTF8.GetBytes(FilePath.Data));
        w.Write(PathMD5, 0, 16);
    }

    private static bool IsMd5Empty(byte[] m)
    {
        foreach (var b in m) if (b != 0) return false;
        return true;
    }
}
