// Based on datfpk fpk/string.go
using System.Buffers.Binary;
using System.Text;

namespace MgsvModBldr.Tools.Fpk;

public sealed class FpkString
{
    public uint   Offset { get; set; }
    public uint   Skip1  { get; set; }
    public uint   Length { get; set; }
    public uint   Skip2  { get; set; }
    public string Data   { get; set; } = string.Empty;

    public const int HeaderSize = 16;

    public void ReadHeader(Stream r)
    {
        Span<byte> b = stackalloc byte[HeaderSize];
        ReadExact(r, b);
        Offset = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(0, 4));
        Skip1  = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(4, 4));
        Length = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(8, 4));
        Skip2  = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(12, 4));
    }

    public void Read(Stream r)
    {
        ReadHeader(r);
        long cur = r.Position;
        r.Seek(Offset, SeekOrigin.Begin);
        var d = new byte[Length];
        ReadExact(r, d);
        r.Seek(cur, SeekOrigin.Begin);
        Data = Encoding.UTF8.GetString(d);
    }

    public void WriteHeader(Stream w)
    {
        Span<byte> b = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(0, 4),  Offset);
        BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(4, 4),  Skip1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(8, 4),  Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.Slice(12, 4), Skip2);
        w.Write(b);
    }

    public void WriteData(Stream w)
    {
        Offset = (uint)w.Position;
        Length = (uint)Encoding.UTF8.GetByteCount(Data);
        var bytes = Encoding.UTF8.GetBytes(Data);
        w.Write(bytes, 0, bytes.Length);
        w.WriteByte(0x00);
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
