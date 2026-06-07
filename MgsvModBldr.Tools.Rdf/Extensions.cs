// Based on RdfTool/Extensions.cs
using System.Globalization;

namespace MgsvModBldr.Tools.Rdf
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
