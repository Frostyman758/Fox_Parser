// Based on RdfTool/Program.cs (enums) — shared types for the .rdf format.
namespace MgsvModBldr.Tools.Rdf
{
    public enum Version : byte
    {
        TPP = 3,
        GZ = 1,
    }

    public enum RadioType : byte
    {
        real_time = 0,
        espionage = 1,
        optional = 2,
        game_over = 3,
        map = 4,
        mission_image = 5,
    }

    // Shim so the vendored structures' `Program.Verbose` references compile
    // unchanged. Default off (no diagnostic Console spam).
    internal static class Program
    {
        public static bool Verbose;
    }
}
