// Test fixture/oracle locations
using System.Xml.Linq;

namespace MgsvModBldr.Tools.Testing;

public static class TestEnv
{
    public const string FixturesDir = @"C:\rsearch\test_fixtures";

    public static string FindDatFpk()
    {
        var env = Environment.GetEnvironmentVariable("DATFPK");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        // modbldr's builder.xml next to the exe: <mgsv_tools><shared><datfpk>
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "")
                      ?? Directory.GetCurrentDirectory();
            var xml = Path.Combine(dir, "builder.xml");
            if (File.Exists(xml))
            {
                var v = XDocument.Load(xml)
                    .Element("mgsv_tools")?.Element("shared")?.Element("datfpk")?.Value;
                if (!string.IsNullOrWhiteSpace(v) && File.Exists(v)) return v;
            }
        }
        catch { /* ignore */ }
        return null;
    }
}
