// GZ fpk entry — SEPARATE from the TPP FpkEntry. One 48-byte record:
//   data offset/size (16B) + GzFpkString header (16B) + path MD5 (16B).
// The file data lives at DataOffset for DataSize bytes and is kept verbatim:
// GZ fpk data is not path-key-encrypted (GzsTool 0.2 reads it raw too), so the
// resolved name has no effect on the extracted bytes.
using System.Buffers.Binary;

namespace MgsvModBldr.Tools.Fpk.Gz;

public sealed class GzFpkEntry
{
    public const int RecordSize = 48;

    public string FilePath => _name.Path;
    public byte[] Data { get; private set; } = System.Array.Empty<byte>();

    private readonly GzFpkString _name = new();

    public void Read(Stream r)
    {
        Span<byte> info = stackalloc byte[16];
        GzFpkString.ReadExact(r, info);
        uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(info.Slice(0, 4));
        int  dataSize   = BinaryPrimitives.ReadInt32LittleEndian(info.Slice(8, 4));

        _name.Read(r);

        Span<byte> md5 = stackalloc byte[16];
        GzFpkString.ReadExact(r, md5);
        _name.Resolve(md5.ToArray());

        long cur = r.Position;
        r.Seek(dataOffset, SeekOrigin.Begin);
        Data = new byte[dataSize < 0 ? 0 : dataSize];
        if (Data.Length > 0) GzFpkString.ReadExact(r, Data);
        r.Seek(cur, SeekOrigin.Begin);
    }
}
