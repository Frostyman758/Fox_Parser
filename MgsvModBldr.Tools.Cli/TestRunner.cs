using System.Diagnostics;
using MgsvModBldr.Tools.Testing;
using MgsvModBldr.Tools.Fsop.Tests;
using MgsvModBldr.Tools.Fox.Tests;
using MgsvModBldr.Tools.Ftex.Tests;
using MgsvModBldr.Tools.Qar.Tests;
using MgsvModBldr.Tools.Fpk.Tests;
using MgsvModBldr.Tools.Pftxs.Tests;
using MgsvModBldr.Tools.Translation.Tests;
using MgsvModBldr.Tools.Twpf.Tests;
using MgsvModBldr.Tools.Mtar.Tests;

namespace MgsvModBldr.Tools.Tests;

/// <summary>
/// Thin aggregator over the per-tool <see cref="IToolTests"/> suites.
/// Each tool owns its own gate + harvest in its sibling .Tests project
/// (so a tool can be lifted out with its verification intact); this just
/// collects them, applies the optional name filter, runs harvest/gate in
/// a fixed order, and prints the summary.
/// </summary>
public static class TestRunner
{
    // Registration order == harvest + run order.
    private static IToolTests[] Tools() => new IToolTests[]
    {
        new FsopTests(),
        new FoxTests(),
        new FtexTests(),
        new QarTests(),
        new FpkTests(),
        new PftxsTests(),
        new SubpTests(),
        new FfntTests(),
        new LangTests(),
        new TwpfTests(),
        new MtarTests(),
    };

    /// <summary>
    /// Run regression tests, optionally scoped to one tool by name
    /// (fsop|fox|ftex|qar|fpk|pftxs|subp). With <paramref name="harvest"/>
    /// true, refresh fixtures first — also scoped to the filter.
    /// </summary>
    public static int Run(bool harvest, string toolFilter = null)
    {
        var all = Tools();
        var tools = all
            .Where(t => toolFilter is null || string.Equals(toolFilter, t.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (tools.Count == 0)
        {
            Console.Error.WriteLine($"Unknown tool '{toolFilter}'. Known: {string.Join(", ", all.Select(t => t.Name))}");
            return 2;
        }

        if (harvest)
        {
            Console.WriteLine($"Harvesting fixtures{(toolFilter is null ? "" : $" ({toolFilter} only)")}...");
            foreach (var t in tools) t.Harvest();
            Console.WriteLine();
        }

        var sw = Stopwatch.StartNew();
        int totalPass = 0, totalFail = 0;
        for (int i = 0; i < tools.Count; i++)
        {
            if (i > 0) Console.WriteLine();
            var (p, f) = tools[i].Run();
            totalPass += p;
            totalFail += f;
        }
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"=== Summary: {totalPass} passed, {totalFail} failed ({sw.ElapsedMilliseconds} ms total) ===");
        return totalFail == 0 ? 0 : 1;
    }
}
