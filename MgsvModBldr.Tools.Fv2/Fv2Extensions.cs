// WriteZeroes helper (FvTwool's BinaryWriter extension).
using System.IO;

namespace MgsvModBldr.Tools.Fv2
{
    internal static class Fv2Extensions
    {
        internal static void WriteZeroes(this BinaryWriter writer, int count)
        {
            writer.Write(new byte[count]);
        }
    }
}
