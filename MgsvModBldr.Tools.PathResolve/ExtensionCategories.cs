// Per-extension target/language directory rules
// 07/07/2026
using System.Collections.Generic;

namespace MgsvModBldr.Tools.PathResolve;

// Ported from real init.lua's AssetConfiguration.RegisterExtensionInfo calls.
// Verified byte-identical between retail (data1.dat) and the prototype exe's
// init.lua on 07/07/2026 - one table serves both targets.
public static class ExtensionCategories
{
    static readonly HashSet<string> TargetOnly = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "bnk","col","demo","demox","dfrm","evb","fclo","fcnp","fdes","fmdl","fmdlb",
        "info","fpk","fpkd","frdv","frig","fstb","ftex","ftexs","gani","lani","mtar",
        "mtard","caar","geom","gskl","nav","nav2","sani","sand","mog","fv2","cani",
        "fmtt","lpsh","ffnt","fova","pftxs","frl","frld","frt","atsh","pcsp","uia",
        "uif","uilb","uigb","fnt","rdf","nta","subp","lba","ladb","lng",
    };

    static readonly HashSet<string> LanguageOnly = new(System.StringComparer.OrdinalIgnoreCase) { "sad", "evfl" };

    static readonly HashSet<string> TargetAndLanguage = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "sbp", "stm", "mas", "wem", "fsm",
    };

    // Windows+DX11 branch overrides. EnableWindowsDX11Texture is always true on
    // PC (IsDiscOrHddImage() hardcoded true in PC release builds), so these are
    // the only reachable overrides - the #Win-only fallback branch never runs.
    static readonly Dictionary<string, string> TargetOverrides = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["ftex"] = "#windx11", ["ftexs"] = "#windx11", ["pftxs"] = "#windx11",
        ["fpk"] = "#windx11", ["fpkd"] = "#windx11",
    };

    const string DefaultTargetDir = "#Win";

    // Static default from SetDefaultCategory("Language","jpn"). Whether it's
    // actually appended for sbp/wem in practice is unverified - separate open
    // item from Phase 0, cheap to correct once procmon-checked.
    public const string LanguageDirectory = "#Jap";

    public static bool HasTarget(string ext) => TargetOnly.Contains(ext) || TargetAndLanguage.Contains(ext);
    public static bool HasLanguage(string ext) => LanguageOnly.Contains(ext) || TargetAndLanguage.Contains(ext);

    public static string TargetDirectory(string ext) =>
        TargetOverrides.TryGetValue(ext, out string dir) ? dir : DefaultTargetDir;
}
