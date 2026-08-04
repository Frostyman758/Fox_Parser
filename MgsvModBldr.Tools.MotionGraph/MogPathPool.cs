// the .gani PathId pool a mog references, and rewriting it
namespace MgsvModBldr.Tools.MotionGraph;

// A mog never stores an animation index. Nodes reach animations through 4-byte
// self-relative pointers (AnimParamBinaryPath) into a pool of 8-byte PathIds, and
// AnimParamBinaryPath::GetPathId rejects any id whose top 16 bits are zero.
//
// The pool is found by following those pointers and keeping the targets that carry a
// .gani extension code — TPP packs it at bit 51 (8074), GZ at bit 52 (typeId 11).
public static class MogPathPool
{
    public const uint TppGaniExt = 8074;
    public const uint GzGaniType = 11;

    public static bool IsGaniId(ulong v) => (v >> 51) == TppGaniExt || (v >> 52) == GzGaniType;

    public sealed class Slot
    {
        public int At;              // where the 8-byte PathId lives
        public ulong Id;
        public int RefCount;        // pointers aimed at it
    }

    public static List<Slot> Find(byte[] b)
    {
        var map = new Dictionary<int, Slot>();
        for (int o = 0; o + 4 <= b.Length; o += 4)
        {
            int t = o + BitConverter.ToInt32(b, o);
            if (t < 0 || t > b.Length - 8 || (t & 7) != 0) continue;
            ulong v = BitConverter.ToUInt64(b, t);
            if (!IsGaniId(v)) continue;
            if (!map.TryGetValue(t, out var s)) map[t] = s = new Slot { At = t, Id = v };
            s.RefCount++;
        }
        var list = new List<Slot>(map.Values);
        list.Sort((x, y) => x.At.CompareTo(y.At));
        return list;
    }

    // Rewrite pool ids in place. Same width, so nothing moves and every offset stays valid.
    // Returns how many slots changed; unmapped slots are left alone.
    public static int Rewrite(byte[] b, IReadOnlyDictionary<ulong, ulong> map, out int unmapped)
    {
        int changed = 0;
        unmapped = 0;
        foreach (var s in Find(b))
        {
            if (map.TryGetValue(s.Id, out var to))
            {
                if (to != s.Id) { BitConverter.GetBytes(to).CopyTo(b, s.At); changed++; }
            }
            else unmapped++;
        }
        return changed;
    }
}
