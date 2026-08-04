// Gani track structures
// Ported verbatim from FoxBrowser.Models.Anim 04/08/2026 — do not re-derive, copy.
namespace MgsvModBldr.Tools.Anim;

// Low-level gani track structures — faithful C# port of fox_gani_types.py
// (TrackHeader / TrackUnit / TrackData / TrackMiniHeader / Gani2TrackData). These
// describe the track LAYOUT (bone-name hash + per-segment type / bit size); the
// keyframe blobs they point at are decoded by AnimBitReader.DecodeSegment.
//
// Layout tracks (v2 CommonInfo, v1 UNIT node) carry only structure (no data_blob);
// per-gani data (v1 inline, v2 TrackMiniHeader) carries the keyframe blobs.

// A tiny little-endian cursor over a byte[] (gani blobs are small; no spans needed).
public sealed class LeReader(byte[] d, int pos = 0)
{
    public byte[] Data = d;
    public int Pos = pos;
    public byte U8() => Data[Pos++];
    public int U16() { int v = Data[Pos] | Data[Pos + 1] << 8; Pos += 2; return v; }
    public short S16() => (short)U16();
    public uint U32() { uint v = (uint)(Data[Pos] | Data[Pos + 1] << 8 | Data[Pos + 2] << 16 | Data[Pos + 3] << 24); Pos += 4; return v; }
    public int S32() => (int)U32();
    public uint U24() { uint v = (uint)(Data[Pos] | Data[Pos + 1] << 8 | Data[Pos + 2] << 16); Pos += 3; return v; }
    public void Skip(int n) => Pos += n;
    public static int Align(int n, int a) => (n + a - 1) / a * a;
}

// One segment within a track: its value type + (for layout) its component bit size,
// and the byte offset (relative to the TrackData entry) of its keyframe blob.
public sealed class TrackData
{
    public const int EntrySize = 8;
    public int DataOffset;             // s32, relative to this entry's position
    public short MsId;                 // s16 motion-segment id
    public GaniSegmentType TdType;     // low 4 bits of the packed byte
    public int NextEntryOffset;        // high 4 bits
    public int ComponentBitSize;       // u8

    public static TrackData Read(LeReader r)
    {
        var t = new TrackData
        {
            DataOffset = r.S32(),
            MsId = r.S16(),
        };
        int typeAndNext = r.U8();
        t.TdType = (GaniSegmentType)(typeAndNext & 0x0F);
        t.NextEntryOffset = (typeAndNext >> 4) & 0x0F;
        t.ComponentBitSize = r.U8();
        return t;
    }
}

// A track = one bone's animation: a StrCode32 name hash + N segments.
public sealed class TrackUnit
{
    public const int BaseSize = 8;
    public uint Name;                  // StrCode32 (bone name hash)
    public int SegmentCount;           // u8
    public int UnitFlags;              // u8 (LOOP=1, HERMITE=2, IS_STATIC=4)
    public List<TrackData> Segments = new();

    public bool IsStatic => (UnitFlags & 0x4) != 0;

    public static TrackUnit Read(LeReader r)
    {
        var u = new TrackUnit { Name = r.U32() };
        u.SegmentCount = r.U8();
        u.UnitFlags = r.U8();
        r.Skip(2);                     // padding
        for (int i = 0; i < u.SegmentCount; i++) u.Segments.Add(TrackData.Read(r));
        return u;
    }
}

// TrackHeader (20B) + unit offsets. The layout-track header for a gani section.
public sealed class TrackHeader
{
    public const int BaseSize = 20;
    public int UnitCount;              // u32
    public int SegmentCount;           // u32
    public int TId;                    // u16
    public int FrameCount;             // u32
    public int FrameRate;              // u32 (byte + 3 pad)
    public List<int> UnitOffsets = new();

    // The runtime reads a SIGNED BYTE at +0x10 (TrackControl::PlayData: sx.d([hdr+0x10].b))
    // and uses it as the per-delta time scale: duration = deltaByte * scale, total =
    // scale * frameCount. Virtually always 1, but carried through for 1:1 fidelity.
    public int FrameScaleByte => (sbyte)(FrameRate & 0xFF);

    public static TrackHeader Read(LeReader r)
    {
        var h = new TrackHeader
        {
            UnitCount = (int)r.U32(),
            SegmentCount = (int)r.U32(),
            TId = r.U16(),
        };
        r.Skip(2);                     // unknown_a, unknown_b
        h.FrameCount = (int)r.U32();
        h.FrameRate = (int)r.U32();
        for (int i = 0; i < h.UnitCount; i++) h.UnitOffsets.Add((int)r.U32());
        return h;
    }
}

// Per-gani v2 segment-data pointer (4B): component bit size + 3-byte self-relative offset.
public sealed class Gani2TrackData
{
    public const int EntrySize = 4;
    public int ComponentBitSize;
    public int DataOffset;             // 3 bytes, relative to this entry's position

    public static Gani2TrackData Read(LeReader r)
    {
        var g = new Gani2TrackData { ComponentBitSize = r.U8() };
        g.DataOffset = (int)r.U24();
        return g;
    }
}

// Per-gani v2 mini-header: frame count + params + per-track unit flags + the
// per-segment Gani2TrackData (component bit size / blob offset) array.
public sealed class TrackMiniHeader
{
    public const int BaseSize = 8;
    public int FrameCount;
    public int ParamCount;
    public List<(uint name, float value)> Params = new();
    public List<int> UnitFlags = new();                 // one per track unit
    public List<Gani2TrackData> SegmentHeaders = new(); // one per (track,segment)

    public static TrackMiniHeader Read(LeReader r, int unitCount, int segmentCount)
    {
        var h = new TrackMiniHeader { FrameCount = (int)r.U32() };
        r.U8();                        // pad0
        h.ParamCount = r.U8();
        r.Skip(2);                     // pad1
        for (int i = 0; i < h.ParamCount; i++) { uint n = r.U32(); float v = BitConverter.ToSingle(r.Data, r.Pos); r.Pos += 4; h.Params.Add((n, v)); }
        for (int i = 0; i < unitCount; i++) h.UnitFlags.Add(r.U8());
        r.Pos = LeReader.Align(r.Pos, 4);
        for (int i = 0; i < segmentCount; i++) h.SegmentHeaders.Add(Gani2TrackData.Read(r));
        r.Skip(16);                    // FSkip(16)
        return h;
    }
}
