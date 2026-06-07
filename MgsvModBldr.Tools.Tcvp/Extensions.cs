// Based on TcvpTool/Extensions.cs
using System.Globalization;

namespace MgsvModBldr.Tools.Tcvp
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
