// Write a QAR from prepared blocks
using System.Buffers.Binary;

namespace MgsvModBldr.Tools.Streaming;

/// <summary>
/// One ready-to-write QAR entry. Either an inline byte block (a freshly encoded
/// modded file) or a reference into a source archive (an unchanged entry, copied
/// verbatim during the write — no decode, no buffering the whole archive in RAM).
/// </summary>
public sealed class PreparedBlock
{
    public ulong PathHash;
    public byte[] Inline;       // non-null => write these bytes
    public string SourcePath;   // else copy Length bytes from SourcePath@SourceOffset
    public long SourceOffset;
    public int Length;

    public static PreparedBlock FromBytes(ulong hash, byte[] bytes) =>
        new() { PathHash = hash, Inline = bytes, Length = bytes.Length };

    public static PreparedBlock FromSource(ulong hash, string path, long offset, int length) =>
        new() { PathHash = hash, SourcePath = path, SourceOffset = offset, Length = length };
}

/// <summary>
/// Writes a QAR from pre-made blocks: lay them out at aligned offsets, build the
/// section table, write the header. Inline blocks are written directly; source
/// refs are streamed straight from the old archive. Byte-identical to
/// QarFile.Write when the same blocks are supplied in the same order.
/// </summary>
public static class QarBlockWriter
{
    public static void Write(string fileName, uint flags, uint version, IReadOnlyList<PreparedBlock> blocks)
    {
        int count = blocks.Count;
        int shift = QarFormat.Shift(flags);
        int alignment = 1 << shift;

        var srcStreams = new Dictionary<string, FileStream>();
        var copyBuf = new byte[1 << 20]; // 1 MiB
        var pad = new byte[alignment];

        try
        {
            using var file = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

            long firstData = QarFormat.AlignUp(QarFormat.HeaderSize + (long)QarFormat.BlockSize * count, alignment);
            file.Seek(firstData, SeekOrigin.Begin);

            var sections = new ulong[count];
            long pos = firstData;
            for (int i = 0; i < count; i++)
            {
                var blk = blocks[i];
                ulong ph = blk.PathHash;
                sections[i] = ((ulong)(pos >> shift)) << 40
                            | (ph & 0xFF) << 32
                            | (ph >> 32 & 0xFFFFFFFFFFUL);

                if (blk.Inline != null)
                {
                    file.Write(blk.Inline, 0, blk.Length);
                }
                else
                {
                    var src = GetStream(srcStreams, blk.SourcePath);
                    src.Seek(blk.SourceOffset, SeekOrigin.Begin);
                    int remaining = blk.Length;
                    while (remaining > 0)
                    {
                        int want = Math.Min(remaining, copyBuf.Length);
                        int got = src.Read(copyBuf, 0, want);
                        if (got == 0) throw new EndOfStreamException($"short copy from {blk.SourcePath}");
                        file.Write(copyBuf, 0, got);
                        remaining -= got;
                    }
                }
                pos += blk.Length;

                long rem = pos % alignment;
                if (rem != 0)
                {
                    int p = (int)(alignment - rem);
                    file.Write(pad, 0, p);
                    pos += p;
                }
            }

            uint blockFileEnd = (uint)(pos >> shift);
            uint offsetFirstFile = (uint)firstData;

            file.Seek(0, SeekOrigin.Begin);
            file.Write(QarFormat.Magic, 0, 4);

            var hdr = new byte[28];
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(0, 4), flags ^ QarFormat.XorTable[0]);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4, 4), (uint)count ^ QarFormat.XorTable[1]);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8, 4), 0u ^ QarFormat.XorTable[2]);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(12, 4), blockFileEnd ^ QarFormat.XorTable[3]);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(16, 4), offsetFirstFile ^ QarFormat.XorTable[0]);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(20, 4), version ^ QarFormat.XorTable[0]);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(24, 4), 0u ^ QarFormat.XorTable[1]);
            file.Write(hdr, 0, hdr.Length);

            var sectionBlob = QarFormat.EncryptSections(sections, version);
            file.Write(sectionBlob, 0, sectionBlob.Length);

            long headerEnd = QarFormat.HeaderSize + (long)QarFormat.BlockSize * count;
            if (firstData > headerEnd)
                file.Write(new byte[firstData - headerEnd], 0, (int)(firstData - headerEnd));
        }
        finally
        {
            foreach (var s in srcStreams.Values) s.Dispose();
        }
    }

    private static FileStream GetStream(Dictionary<string, FileStream> cache, string path)
    {
        if (!cache.TryGetValue(path, out var fs))
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            cache[path] = fs;
        }
        return fs;
    }
}
