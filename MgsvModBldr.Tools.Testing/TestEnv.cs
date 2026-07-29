// Test fixture/oracle locations
using MgsvModBldr.Core;

namespace MgsvModBldr.Tools.Testing;

public static class TestEnv
{
    public const string FixturesDir = @"C:\rsearch\test_fixtures";

    public static string FindDatFpk()
    {
        var env = Environment.GetEnvironmentVariable("DATFPK");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        try
        {
            var state = new BuildState();
            BuildStateIo.Load(state, BuildStateIo.DefaultPath());
            if (!string.IsNullOrWhiteSpace(state.DatFpk) && File.Exists(state.DatFpk)) return state.DatFpk;
        }
        catch { /* ignore */ }
        return null;
    }
}
