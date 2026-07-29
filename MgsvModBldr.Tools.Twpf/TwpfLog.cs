// Twpf verbose logging switch
namespace MgsvModBldr.Tools.Twpf
{
    // Replaces the original tool's Program.IsVerbose so the vendored
    // Read/Write code keeps its optional diagnostic logging.
    internal static class TwpfLog
    {
        public static bool IsVerbose;
    }
}
