// Read the shared track layout out of a v2 .trk
using System;
using System.Text;

namespace MgsvModBldr.Tools.Mtar.Transcode
{
    /// <summary>
    /// The layout a v2 mtar shares across all its ganis. The .trk is a 16-byte wrapper
    /// (magic, sizes) followed by the same TrackHeader + TrackUnit tables a v1 gani keeps
    /// inline — so a v1 source and a v2 template can be compared unit for unit.
    /// </summary>
    public sealed class TrackLayout
    {
        /// <summary>TrackHeader starts after the .trk's 16-byte wrapper.</summary>
        private const int HeaderAt = 0x10;

        public int UnitCount;
        public int SegmentCount;
        public int FrameScaleByte;

        /// <summary>Same shape as V1Gani.Signature so the two can be compared directly.</summary>
        public string Signature = "";

        public static TrackLayout FromTrk(byte[] d)
        {
            if (d is null || d.Length < HeaderAt + 20) return null;
            int units = (int)BitConverter.ToUInt32(d, HeaderAt);
            int segs = (int)BitConverter.ToUInt32(d, HeaderAt + 4);
            if (units <= 0 || units > 4096) return null;
            if (HeaderAt + 20 + units * 4 > d.Length) return null;

            var t = new TrackLayout
            {
                UnitCount = units,
                SegmentCount = segs,
                FrameScaleByte = (sbyte)(BitConverter.ToUInt32(d, HeaderAt + 16) & 0xFF),
            };

            var sb = new StringBuilder();
            for (int i = 0; i < units; i++)
            {
                int off = (int)BitConverter.ToUInt32(d, HeaderAt + 20 + i * 4);
                int p = HeaderAt + off;
                if (p < 0 || p + 8 > d.Length) return null;
                sb.Append(BitConverter.ToUInt32(d, p).ToString("x8")).Append(':');
                int n = d[p + 4];
                for (int s = 0; s < n; s++)
                {
                    int e = p + 8 + s * 8;
                    if (e + 8 > d.Length) return null;
                    sb.Append(d[e + 6] & 0x0F).Append(',');
                }
                sb.Append(';');
            }
            t.Signature = sb.ToString();
            return t;
        }
    }
}
