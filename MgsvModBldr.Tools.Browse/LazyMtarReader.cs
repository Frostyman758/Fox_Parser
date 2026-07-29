// Index-only mtar reader driving the verified MtarFile readers
using System.Buffers.Binary;
using MgsvModBldr.Tools.Mtar.Mtar;

namespace MgsvModBldr.Tools.Browse;

// Rather than reimplement the fiddly v1/v2 + chunk-scan layout, this DRIVES
// the verified MtarFile/MtarFile2 readers to parse the index, then records
// each item's on-disk region — so gani/trk/chnk/exchnk payloads are pulled on
// demand instead of materialised up front. Mirrors MtarBrowse.Read
// item-for-item (same names, same order). The only block whose size needs a
// scan is the rare .enchnk, so that tiny block is read once at open.
// Byte-for-byte identical — verified by the test.
internal static class LazyMtarReader
{
    internal sealed class Item
    {
        public string  Name = "";
        public long    Offset;
        public int     Size;
        public byte[]? Eager;   // set only for .enchnk (size requires a scan)
    }

    public static List<Item> Read(Stream s)
    {
        var items = new List<Item>();
        int type = DetectType(s);            // mirrors MtarBrowse.DetectType
        if (type == 1)
        {
            var f = new MtarFile();
            s.Position = 0; f.Read(s);
            foreach (var g in f.files)        // g.name already ends in ".gani"
                items.Add(new Item { Name = g.name, Offset = g.offset, Size = g.size });
        }
        else
        {
            var f = new MtarFile2();
            s.Position = 0; f.Read(s);
            items.Add(new Item { Name = "track.trk", Offset = f.mtarTrack.offset, Size = (int)(f.mtarTrack.length + 0x10) });
            if (f.mtarTrack.chunkOffset > 0)
                items.Add(new Item { Name = "chunk.chnk", Offset = f.mtarChunk.offset, Size = f.mtarChunk.size });

            foreach (var g in f.files)
            {
                var stem = g.name;            // resolved path or hash (no extension)
                items.Add(new Item { Name = stem + ".gani", Offset = g.offset, Size = g.size });
                if (g.exChunkSize != 0)       // exchnk sits right after the gani data
                    items.Add(new Item { Name = stem + ".exchnk", Offset = (long)g.offset + g.size, Size = g.exChunkSize });
                if (g.endChunkOffset != 0)    // size needs a sentinel scan -> read its (tiny) block now
                    items.Add(new Item { Name = stem + ".enchnk", Eager = g.ReadEndChunkData(s) });
            }
        }
        return items;
    }

    // 1 = type 1 (first entry's data magic 0xBFCA2D2), else 2. .mtar has no header.
    private static int DetectType(Stream s)
    {
        if (s.Length < 0x2C) return 2;
        Span<byte> b = stackalloc byte[4];
        s.Position = 0x28; ReadExact(s, b);
        uint firstOffset = BinaryPrimitives.ReadUInt32LittleEndian(b);
        if (firstOffset + 4 > (uint)s.Length) return 2;
        s.Position = firstOffset; ReadExact(s, b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b) == 0x0BFCA2D2u ? 1 : 2;
    }

    private static void ReadExact(Stream s, Span<byte> buf)
    {
        int n = 0;
        while (n < buf.Length) { int r = s.Read(buf[n..]); if (r == 0) throw new EndOfStreamException(); n += r; }
    }
}
