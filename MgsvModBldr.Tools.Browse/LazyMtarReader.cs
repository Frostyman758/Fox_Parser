// Index-only mtar reader driving the verified MtarFile readers
using System.Buffers.Binary;
using MgsvModBldr.Tools.Mtar.Mtar;

namespace MgsvModBldr.Tools.Browse;

// Rather than reimplement the fiddly v1/v2 + chunk-scan layout, this DRIVES
// the verified MtarFile/MtarFile2 readers to parse the index, then records
// each item's on-disk region — so gani/trk/chnk/exchnk payloads are pulled on
// demand instead of materialised up front. Mirrors MtarBrowse.Read
// item-for-item (same order). The only block whose size needs a scan is the
// rare .enchnk, so that tiny block is read once at open.
//
// Names differ from MtarBrowse on purpose: this is the BROWSE listing, so
// entries are named from the shipped dictionaries (see Stem) and shown as bare
// leaves. MtarBrowse keeps NameResolver's key format because unpack/repack has
// to reconstruct the hash from the name.
internal static class LazyMtarReader
{
    internal sealed class Item
    {
        public string  Name = "";
        public ulong   Hash;    // the gani's asset id (0 for .trk/.chnk)
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
                items.Add(new Item { Name = Stem(g.hash, g.name) + ".gani", Hash = g.hash, Offset = g.offset, Size = g.size });
        }
        else
        {
            var f = new MtarFile2();
            s.Position = 0; f.Read(s);
            // CommonInfo is decoded, not carried — same as MtarBrowse, surface the
            // track node's rebuilt bytes rather than a file region.
            var trk = f.TrackNodeBytes();
            if (trk.Length > 0) items.Add(new Item { Name = "track.trk", Eager = trk });

            foreach (var g in f.files)
            {
                var stem = Stem(g.hash, g.name);
                items.Add(new Item { Name = stem + ".gani", Hash = g.hash, Offset = g.offset, Size = g.size });
                if (g.motionPointsSize != 0)  // motion-point tracks sit right after the gani data
                    items.Add(new Item { Name = stem + ".mtp", Hash = g.hash, Offset = (long)g.offset + g.size, Size = g.motionPointsSize });
                if (g.endChunkOffset != 0)    // size needs a sentinel scan -> read its (tiny) block now
                    items.Add(new Item { Name = stem + ".enchnk", Hash = g.hash, Eager = g.ReadEndChunkData(s) });
            }
        }
        return items;
    }

    // The entry hash IS the animation's asset id, so the shipped dictionaries name
    // it: TPP through qar_dictionary (PathCode64), GZ through GzHashNames (48-bit).
    // Leaf only — an mtar lists flat, and a full "/Assets/…" path would bury a
    // 2,400-clip archive under six folders. Falls back to the reader's own name.
    private static string Stem(ulong hash, string fallback)
    {
        if (hash != 0)
        {
            if (GzHashNames.ResolveLeaf(hash) is { } gz) return StripExtension(gz);
            var dict = QarNameDictionary.Get();
            if (dict is not null)
            {
                var full = dict.Resolve(hash, out bool found);
                if (found) return StripExtension(full[(full.LastIndexOfAny(Seps) + 1)..]);
            }
        }
        return StripExtension(fallback);
    }

    private static readonly char[] Seps = { '/', '\\' };

    private static string StripExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
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
