using System.Buffers.Binary;

namespace MgsvModBldr.Tools.G0s;

/// <summary>One .g0s entry. <see cref="Size"/> is the raw on-disk size (it
/// includes the 8-byte inner header for inner-encrypted entries).</summary>
public sealed class G0sEntry
{
    public ulong Hash;
    public uint Offset;          // 16-byte units
    public uint Size;            // raw on-disk size
    public string FilePath = ""; // resolved on-disk path (with extension)
    public bool FileNameFound;
    public uint? InnerKey;       // set if inner-encrypted (preserved for byte-exact repack)
}

/// <summary>
/// GZ QAR (.g0s) archive — footer-based (no header). Layout:
///   [data blocks, 16-aligned] [entry table: N×16] [uint sizeSum] [align 16]
///   [footer: count|0x71610000|entryBlockOffset|0|20]
/// The last 4 bytes are the footer size (20); the 20-byte footer before it
/// gives the entry count and the entry-table offset (×16). Each 16-byte
/// entry is hash(8) | offset(4, ×16) | size(4). Ported from GzsTool 0.2.
/// </summary>
public sealed class G0sArchive
{
    public const int FooterSize = 20;
    public const uint FooterMagic1 = 0x71610000;

    public string Name = "";
    public List<G0sEntry> Entries { get; } = new();

    /// <summary>Read the footer + entry table (no data).</summary>
    public static G0sArchive ReadIndex(Stream input)
    {
        Span<byte> b4 = stackalloc byte[4];
        input.Seek(-4, SeekOrigin.End);
        ReadExact(input, b4);
        if (BinaryPrimitives.ReadInt32LittleEndian(b4) != FooterSize)
            throw new InvalidDataException("Invalid g0s footer (size != 20).");

        Span<byte> f = stackalloc byte[FooterSize];
        input.Seek(-FooterSize, SeekOrigin.End);
        ReadExact(input, f);
        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(f.Slice(0, 4));
        int entryBlockOffset = BinaryPrimitives.ReadInt32LittleEndian(f.Slice(8, 4));

        var arc = new G0sArchive();
        input.Seek(16L * entryBlockOffset, SeekOrigin.Begin);
        Span<byte> e = stackalloc byte[16];
        for (int i = 0; i < entryCount; i++)
        {
            ReadExact(input, e);
            arc.Entries.Add(new G0sEntry
            {
                Hash   = BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(0, 8)),
                Offset = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(8, 4)),
                Size   = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(12, 4)),
            });
        }
        return arc;
    }

    /// <summary>
    /// Decrypt a raw entry blob (outer pass, then optional inner pass) into
    /// the plaintext file bytes. Returns the inner key if the entry was
    /// inner-encrypted (needed to re-encrypt it on repack).
    /// </summary>
    public static (byte[] data, uint? innerKey) Decrypt(byte[] raw, uint offset)
    {
        G0sCrypto.DeEncryptQar(raw, offset); // in place, symmetric
        if (raw.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(raw) == G0sCrypto.InnerKeyMagic)
        {
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4));
            var inner = new byte[raw.Length - 8];
            Array.Copy(raw, 8, inner, 0, inner.Length);
            return (G0sCrypto.DeEncrypt(inner, key), key);
        }
        return (raw, null);
    }

    /// <summary>
    /// Re-encrypt plaintext into the raw on-disk blob: inner cipher (if a key
    /// is set) wrapped with magic+key, then the outer pass for this offset.
    /// This is the half GzsTool 0.2 stubbed out (its // TODO).
    /// </summary>
    public static byte[] Encrypt(byte[] plaintext, uint offset, uint? innerKey)
    {
        byte[] blob;
        if (innerKey is uint key)
        {
            var inner = G0sCrypto.DeEncrypt(plaintext, key); // symmetric -> re-encrypts
            blob = new byte[8 + inner.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0, 4), G0sCrypto.InnerKeyMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4, 4), key);
            Array.Copy(inner, 0, blob, 8, inner.Length);
        }
        else
        {
            blob = (byte[])plaintext.Clone(); // DeEncryptQar mutates; don't touch the caller's buffer
        }
        G0sCrypto.DeEncryptQar(blob, offset);
        return blob;
    }

    /// <summary>Cheaply test whether an entry is inner-encrypted from its first 8 raw bytes.</summary>
    public static bool PeekIsInner(byte[] first8, uint offset)
    {
        var b = (byte[])first8.Clone();
        G0sCrypto.DeEncryptQar(b, offset); // block 0 decrypts correctly on its own
        return b.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(b) == G0sCrypto.InnerKeyMagic;
    }

    /// <summary>On-disk blob size for a plaintext of the given length (+8 if inner-encrypted).</summary>
    public static long BlobSize(long plaintextLength, bool inner) => inner ? plaintextLength + 8 : plaintextLength;

    /// <summary>Convert an entry FilePath ("/Fox/..foo.lua" or "deadbeef.lua") to an OS relative path.</summary>
    public static string OnDiskRelPath(string filePath)
    {
        var p = filePath.StartsWith("/") ? filePath.Substring(1) : filePath;
        return p.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
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
