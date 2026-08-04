// FOXMOTIONGRAPH (.mog) parser
namespace MgsvModBldr.Tools.MotionGraph;

// Every offset in a mog is self-relative: target = address of the field + its value.
public sealed class MogFile
{
    public const string Signature = "FOXMOTIONGRAPH";
    public const int GraphHeaderSize = 0x38;
    public const int StateNodeSize = 0x48;
    public const int BlendNodeSize = 0x2c;
    public const int EdgeSize = 0x28;
    public const uint TagMapParam = 0x185ebb9f;
    public const uint GraphParam = 0x859bd53e;

    public byte[] Raw;
    public uint Unknown10, GraphCount, DefaultAnimParamsCount, ParamsRelated;
    public int Unknown14At, DefaultAnimParamsAt, ParamsAt;
    public byte AnimLayerCount, UnknownD;
    public List<MogGraph> Graphs = [];
    public List<MogParam> Params = [];
    public ulong[] Tags = [];

    public static int Rel(byte[] b, int o) => o + BitConverter.ToInt32(b, o);
    static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
    internal static ulong U64(byte[] b, int o) => BitConverter.ToUInt64(b, o);
    static bool In(byte[] b, int o, int n) => o >= 0 && o <= b.Length - n;

    public static MogFile Read(byte[] b)
    {
        if (b.Length < 0x40 || System.Text.Encoding.ASCII.GetString(b, 0, 14) != Signature)
            throw new InvalidDataException("not a FOXMOTIONGRAPH file");

        var m = new MogFile
        {
            Raw = b,
            Unknown10 = U32(b, 0x10),
            Unknown14At = Rel(b, 0x14),
            AnimLayerCount = b[0x18],
            UnknownD = b[0x19],
            GraphCount = U32(b, 0x1c),
            DefaultAnimParamsCount = U32(b, 0x24),
            DefaultAnimParamsAt = Rel(b, 0x28),
            ParamsRelated = U32(b, 0x2c),
            ParamsAt = Rel(b, 0x30),
        };

        int gh = Rel(b, 0x20);
        for (int i = 0; i < m.GraphCount && In(b, gh + i * GraphHeaderSize, GraphHeaderSize); i++)
            m.Graphs.Add(MogGraph.Read(b, i, gh + i * GraphHeaderSize));

        // param chain: {i32 next(self-rel), u32 name, u32 count, i32 dataOffset}
        for (int o = m.ParamsAt, guard = 0; In(b, o, 16) && guard < 256; guard++)
        {
            var p = new MogParam
            {
                At = o,
                Name = U32(b, o + 4),
                Count = U32(b, o + 8),
                DataAt = (o + 0xc) + BitConverter.ToInt32(b, o + 0xc),
            };
            m.Params.Add(p);
            if (p.Name == TagMapParam && In(b, p.DataAt, (int)p.Count * 8))
            {
                m.Tags = new ulong[p.Count];
                for (int k = 0; k < p.Count; k++) m.Tags[k] = U64(b, p.DataAt + k * 8);
            }
            int next = BitConverter.ToInt32(b, o);
            if (next == 0) break;
            o += next;
        }
        return m;
    }
}

public sealed class MogParam
{
    public int At, DataAt;
    public uint Name, Count;
}
