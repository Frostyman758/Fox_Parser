// In-memory, read-only browse façade for .mtar (Motion Archive) — mirrors the
// extraction MtarConverter/MtarFile(2) perform to disk, but yields the files as
// (name, bytes) pairs in memory and WITHOUT XmlSerializer. Drives the existing
// MtarFile/MtarFile2 readers (so the chunk-size scan logic is reused verbatim).
//
//   v1 -> a .gani per entry
//   v2 -> a .trk, an optional .chnk, and per gani a .gani (+ optional .exchnk /
//         .enchnk). Names resolve via NameResolver (hash hex when no
//         mtar_dictionary.txt is present).
using System.Buffers.Binary;
using MgsvModBldr.Tools.Mtar.Mtar;

namespace MgsvModBldr.Tools.Mtar;

public sealed class MtarItem
{
    public string Name = "";
    public byte[] Data = System.Array.Empty<byte>();
}

public static class MtarBrowse
{
    public static List<MtarItem> Read(byte[] bytes)
    {
        var items = new List<MtarItem>();
        using var s = new MemoryStream(bytes, writable: false);

        if (DetectType(bytes) == 1)
        {
            var f = new MtarFile();
            f.Read(s);
            foreach (var g in f.files)                       // g.name already ends in ".gani"
                items.Add(new MtarItem { Name = g.name, Data = g.ReadData(s) });
        }
        else
        {
            var f = new MtarFile2();
            f.Read(s);
            items.Add(new MtarItem { Name = "track.trk", Data = f.mtarTrack.ReadData(s) });
            if (f.mtarTrack.chunkOffset > 0)
                items.Add(new MtarItem { Name = "chunk.chnk", Data = f.mtarChunk.ReadData(s) });

            foreach (var g in f.files)
            {
                var stem = g.name;                            // resolved path or hash (no extension)
                items.Add(new MtarItem { Name = stem + ".gani", Data = g.ReadData(s) });
                if (g.exChunkSize != 0)
                    items.Add(new MtarItem { Name = stem + ".exchnk", Data = g.ReadExChunkData(s) });
                if (g.endChunkOffset != 0)
                    items.Add(new MtarItem { Name = stem + ".enchnk", Data = g.ReadEndChunkData(s) });
            }
        }
        return items;
    }

    // 1 = Mtar type 1, 2 = Mtar type 2 (matches MtarConverter.GetMtarType: the
    // first entry's data magic 0xBFCA2D2 marks type 1). .mtar has no header magic.
    private static int DetectType(byte[] d)
    {
        if (d.Length < 0x2C) return 2;
        uint firstOffset = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0x28, 4));
        if (firstOffset + 4 > (uint)d.Length) return 2;
        return BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)firstOffset, 4)) == 0x0BFCA2D2u ? 1 : 2;
    }
}
