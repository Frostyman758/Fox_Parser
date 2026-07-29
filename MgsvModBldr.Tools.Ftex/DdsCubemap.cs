// DDS cubemap caps flags
using System;

namespace MgsvModBldr.Tools.Ftex.Dds.Enum
{
    [Flags]
    public enum DdsCubemap
    {
        PositiveX = 0x00000600,

        NegativeX = 0x00000a00,

        PositiveY = 0x00001200,

        NegativeY = 0x00002200,

        PositiveZ = 0x00004200,

        NegativeZ = 0x00008200,

        AllFaces = PositiveX | NegativeX | PositiveY | NegativeY | PositiveZ | NegativeZ
    }
}
