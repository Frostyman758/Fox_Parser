// Based on FvTwool/Vector4.cs
using System;

namespace MgsvModBldr.Tools.Fv2
{
    public class Vector4
    {
        public float x { get; set; }
        public float y { get; set; }
        public float z { get; set; }
        public float w { get; set; }

        public float this[int index]
        {
            get
            {
                switch(index)
                {
                    case 0: return x;
                    case 1: return y;
                    case 2: return z;
                    case 3: return w;
                    default: throw new ArgumentOutOfRangeException();
                }
            }
            set
            {
                switch(index)
                {
                    case 0: x = value; break;
                    case 1: y = value; break;
                    case 2: z = value; break;
                    case 3: w = value; break;
                    default: throw new ArgumentOutOfRangeException();
                }
            }
        }

        public override string ToString() => $"({x}, {y}, {z}, {w})";
    }
}
