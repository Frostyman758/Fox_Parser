// Based on LangTool/Utility/Fox.cs
using CityHash = MgsvModBldr.Tools.GameHashing.GameCityHash;

namespace MgsvModBldr.Tools.Translation.Lang
{
    public static class Fox
    {
        public static uint GetStrCode32(string text)
        {
            const ulong seed0 = 0x9ae16a3b2f90404f;
            ulong seed1 = text.Length > 0 ? (uint)((text[0]) << 16) + (uint)text.Length : 0;
            return (uint)(CityHash.CityHash64WithSeeds(text + "\0", seed0, seed1) & 0xFFFFFFFF);
        }
    }
}
