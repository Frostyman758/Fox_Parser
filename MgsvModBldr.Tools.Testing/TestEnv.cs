using MgsvModBldr.Core;

namespace MgsvModBldr.Tools.Testing;

/// <summary>
/// Environment/config locations shared by the per-tool tests: the
/// fixtures cache and the datfpk reference oracle (whose path the
/// modbldr GUI stores in builder.xml).
/// </summary>
public static class TestEnv
{
    /// <summary>Cached real-file fixtures, refreshed by Harvest().</summary>
    public const string FixturesDir = @"C:\rsearch\test_fixtures";

    /// <summary>
    /// Locate datfpk (the QAR/FPK reference extractor). Honours the
    /// DATFPK env var first, then falls back to whatever the modbldr
    /// GUI is configured with in builder.xml. Null if unavailable.
    /// </summary>
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
