using System.Text;

namespace MgsvModBldr.Tools.Cli;

/// <summary>
/// The FOX splash. Loads the ASCII fox (shipped loose as fox_logo.txt next
/// to the exe) and renders it: instantly on launch (no args), or "drawn
/// out" line-by-line in a per-tool colour as run feedback. Geometry never
/// changes — only the colour shifts per tool. Skipped entirely when stdout
/// is redirected (pipes / the test harness) so scripted output stays clean.
/// </summary>
internal static class FoxLogo
{
    public static readonly ConsoleColor DefaultColor = ConsoleColor.DarkYellow; // Fox Engine orange

    // Each tool gets its own colour; the first 15 are distinct, the sound/
    // shader trio at the end reuse a sibling's hue.
    private static readonly Dictionary<string, ConsoleColor> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fox"]  = ConsoleColor.Cyan,        ["fsop"] = ConsoleColor.Magenta,
        ["ftex"] = ConsoleColor.Green,       ["qar"]  = ConsoleColor.Yellow,
        ["fpk"]  = ConsoleColor.Blue,        ["pftxs"]= ConsoleColor.DarkCyan,
        ["subp"] = ConsoleColor.Red,         ["ffnt"] = ConsoleColor.DarkGreen,
        ["lng"]  = ConsoleColor.DarkMagenta, ["twpf"] = ConsoleColor.DarkBlue,
        ["mtar"] = ConsoleColor.DarkYellow,  ["spch"] = ConsoleColor.Gray,
        ["tcvp"] = ConsoleColor.DarkRed,     ["rdf"]  = ConsoleColor.White,
        ["fv2"]  = ConsoleColor.DarkGray,    ["hlsl"] = ConsoleColor.Magenta,
        ["sbp"]  = ConsoleColor.Green,       ["stp"]  = ConsoleColor.Cyan,
    };

    public static ConsoleColor ColorFor(string tool) =>
        tool is not null && Colors.TryGetValue(tool, out var c) ? c : DefaultColor;

    private static string[] _lines;
    private static string[] Lines => _lines ??= Load();

    private static string[] Load()
    {
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, "fox_logo.txt");
            if (File.Exists(p)) return File.ReadAllLines(p);
        }
        catch { /* fall through */ }
        return new[] { "", "  ███  F O X  ███", "" };
    }

    private static bool _utf8;
    private static void EnsureUtf8()
    {
        if (_utf8) return;
        _utf8 = true;
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* non-tty */ }
    }

    /// <summary>Print the logo instantly (launch splash).</summary>
    public static void Show(ConsoleColor color)
    {
        if (Console.IsOutputRedirected) return;
        EnsureUtf8();
        var prev = Console.ForegroundColor;
        try { Console.ForegroundColor = color; foreach (var l in Lines) Console.WriteLine(l); }
        finally { Console.ForegroundColor = prev; }
    }

    /// <summary>
    /// Reveal the logo line-by-line in <paramref name="color"/> as run
    /// feedback. No-op when redirected. Paced gently; quick enough to feel
    /// like a flourish, not a wait.
    /// </summary>
    public static void DrawOut(ConsoleColor color)
    {
        if (Console.IsOutputRedirected) return;
        EnsureUtf8();
        var prev = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            foreach (var l in Lines)
            {
                Console.WriteLine(l);
                Thread.Sleep(6);
            }
        }
        finally { Console.ForegroundColor = prev; }
        Console.WriteLine();
    }
}
