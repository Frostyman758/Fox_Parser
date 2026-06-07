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
            if (File.Exists(p)) return Downscale(File.ReadAllLines(p), TargetWidth());
        }
        catch { /* fall through */ }
        return new[] { "", "  ███  F O X  ███", "" };
    }

    /// <summary>
    /// Render width (chars) for the splash. The art file stays full-res;
    /// it's downscaled to this on display so the fox shows compact without
    /// the terminal having to be zoomed out. Override with FOX_LOGO_WIDTH.
    /// </summary>
    private static int TargetWidth()
    {
        var env = Environment.GetEnvironmentVariable("FOX_LOGO_WIDTH");
        if (int.TryParse(env, out var w) && w >= 12 && w <= 300) return w;
        return 46;
    }

    // Area-average downscale of the block-art fox to `targetWidth` columns,
    // preserving aspect (same factor for rows + cols), mapped onto a 5-level
    // shade ramp. Returns the source unchanged if it's already that small.
    private static readonly char[] Ramp = { ' ', '░', '▒', '▓', '█' };

    private static string[] Downscale(string[] src, int targetWidth)
    {
        int h = src.Length;
        int w = 0;
        foreach (var l in src) if (l.Length > w) w = l.Length;
        if (h == 0 || w <= targetWidth) return src;

        double scale = (double)w / targetWidth;
        int oh = Math.Max(1, (int)Math.Round(h / scale));
        var outLines = new string[oh];
        var sb = new System.Text.StringBuilder(targetWidth);

        for (int oy = 0; oy < oh; oy++)
        {
            sb.Clear();
            int y0 = (int)(oy * scale);
            int y1 = (int)Math.Min(h, (oy + 1) * scale);
            if (y1 <= y0) y1 = Math.Min(h, y0 + 1);

            for (int ox = 0; ox < targetWidth; ox++)
            {
                int x0 = (int)(ox * scale);
                int x1 = (int)Math.Min(w, (ox + 1) * scale);
                if (x1 <= x0) x1 = Math.Min(w, x0 + 1);

                double sum = 0; int n = 0;
                for (int yy = y0; yy < y1; yy++)
                {
                    var line = src[yy];
                    for (int xx = x0; xx < x1; xx++)
                    {
                        sum += Weight(xx < line.Length ? line[xx] : ' ');
                        n++;
                    }
                }
                double a = n > 0 ? sum / n : 0;
                int idx = a < 0.10 ? 0 : a < 0.28 ? 1 : a < 0.50 ? 2 : a < 0.72 ? 3 : 4;
                sb.Append(Ramp[idx]);
            }
            outLines[oy] = sb.ToString().TrimEnd();
        }
        return outLines;
    }

    // Ink coverage of each art glyph (blocks are graded; everything else
    // non-space is a mid-weight edge/detail char).
    private static double Weight(char c) => c switch
    {
        ' '  => 0.0,
        '█'  => 1.0,
        '▓'  => 0.82,
        '▒'  => 0.55,
        '░'  => 0.30,
        _    => 0.5,
    };

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
                Thread.Sleep(14);
            }
        }
        finally { Console.ForegroundColor = prev; }
        Console.WriteLine();
    }
}
