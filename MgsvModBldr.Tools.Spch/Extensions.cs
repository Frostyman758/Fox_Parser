// Based on SpchTool/Extensions.cs
using System.Globalization;

namespace MgsvModBldr.Tools.Spch
{
    public static class Extensions
    {
        public static float ParseFloatRoundtrip(string text)
        {
            if (text == "-0")
            {
                return -0f;
            }

            return float.Parse(text, CultureInfo.InvariantCulture);
        }
    }
}
