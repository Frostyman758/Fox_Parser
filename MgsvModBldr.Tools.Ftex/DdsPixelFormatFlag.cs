// DDS pixel format flags
using System;

namespace MgsvModBldr.Tools.Ftex.Dds.Enum
{
    [Flags]
    public enum DdsPixelFormatFlag : uint
    {
        Alpha = 0x00000002,

        FourCc = 0x00000004,

        Rgb = 0x00000040,

        Rgba = 0x00000041,

        Luminance = 0x00020000,

        Normal = 0x80000000
    }
}
