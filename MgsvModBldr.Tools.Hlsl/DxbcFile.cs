// DXBC container chunk reader
using System.Text;

namespace MgsvModBldr.Tools.Hlsl;

public sealed class DxbcFile
{
    public byte[] Bytes { get; }
    public readonly List<(string fourCC, int offset, int size)> Chunks = new();

    public DxbcFile(byte[] bytes)
    {
        Bytes = bytes;
        if (bytes.Length < 32 || Encoding.ASCII.GetString(bytes, 0, 4) != "DXBC")
            throw new InvalidDataException("Not a DXBC container");
        int chunkCount = BitConverter.ToInt32(bytes, 28);
        for (int i = 0; i < chunkCount; i++)
        {
            int off = BitConverter.ToInt32(bytes, 32 + 4 * i);
            string cc = Encoding.ASCII.GetString(bytes, off, 4);
            int size = BitConverter.ToInt32(bytes, off + 4);
            Chunks.Add((cc, off + 8, size)); // store payload offset
        }
    }

    public static DxbcFile Read(string path) => new(File.ReadAllBytes(path));

    public (int offset, int size)? Chunk(string fourCC)
    {
        foreach (var (cc, off, size) in Chunks)
            if (cc == fourCC) return (off, size);
        return null;
    }
}
