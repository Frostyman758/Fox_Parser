// DDS surface caps flags
using System;

namespace MgsvModBldr.Tools.Ftex.Dds.Enum
{
    [Flags]
    public enum DdsSurfaceFlags
    {
        Texture = 0x00001000,

        MipMap = 0x00400008,

        CubeMap = 0x00000008
    }
}
