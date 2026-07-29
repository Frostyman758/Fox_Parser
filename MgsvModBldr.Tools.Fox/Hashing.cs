// FoxTool StrCode hashing
using System;
using MgsvModBldr.Core;

namespace MgsvModBldr.Tools.Fox
{
    // Atvaark's original imported the Atvaark.CityHash NuGet package
    // (vendored as the 'CityHash.CityHash' static class). We already
    // ship the same hash inside MgsvModBldr.Core — re-using avoids
    // duplicate code and keeps Fox hashes consistent with the QAR
    // PathCode used elsewhere.
    internal static class Hashing
    {
        internal static ulong HashString(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            byte[] bytes = Constants.StringEncoding.GetBytes(text + "\0");
            const ulong seed0 = 0x9ae16a3b2f90404f;
            ulong seed1 = bytes.Length > 0 ? (uint) ((bytes[0]) << 16) + (uint) (bytes.Length - 1) : 0;
            ulong hash = CityHash64.CityHash64WithSeeds(bytes, seed0, seed1) & 0xFFFFFFFFFFFF;
            return hash;
        }
    }
}
