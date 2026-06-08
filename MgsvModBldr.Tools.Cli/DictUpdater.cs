using System.Net.Http;

namespace MgsvModBldr.Tools.Cli;

/// <summary>
/// Refreshes the string dictionaries in <c>dict/</c> from caplag's
/// mgsv-lookup-strings repo (default) or a custom fork (<c>-repo</c>).
/// Downloads in parallel and only rewrites files whose bytes changed.
/// The mapping below tracks the repo's layout / naming (which differs from
/// our older-tool dict names — e.g. spch_labelname -> spch_label, and the
/// FNV-hashed voiceevent/voiceid -> our spch_fnv_* names).
/// </summary>
internal static class DictUpdater
{
    private const string DefaultRepo = "kapuragu/mgsv-lookup-strings";
    private const string DefaultBranch = "master";

    // (our dict/ filename, path within the repo)
    private static readonly (string local, string remote)[] Map =
    {
        ("qar_dictionary.txt",                "GzsTool/qar_dictionary.txt"),
        ("gzs_dictionary.txt",                "GzsTool/gzs_dictionary.txt"),
        ("lang_dictionary.txt",               "LangTool/lang_dictionary.txt"),
        ("mtar_dictionary.txt",               "MtarTool/mtar_dictionary.txt"),
        ("spch_label_dictionary.txt",         "spch/Dictionaries/spch_labelname_dictionary.txt"),
        ("spch_voicetype_dictionary.txt",     "spch/Dictionaries/spch_voicetype_dictionary.txt"),
        ("spch_anim_dictionary.txt",          "spch/Dictionaries/spch_animationact_dictionary.txt"),
        ("spch_fnv_voiceevent_dictionary.txt","spch/Dictionaries/spch_voiceevent_dictionary.txt"),
        ("spch_fnv_voiceid_dictionary.txt",   "spch/Dictionaries/spch_voiceid_dictionary.txt"),
        ("rdf_label_dictionary.txt",          "rdf/Dictionaries/rdf_labelname_dictionary.txt"),
        ("rdf_optionalset_dictionary.txt",    "rdf/Dictionaries/rdf_optionalsetname_dictionary.txt"),
        ("rdf_dialogueevent_dictionary.txt",  "rdf/Dictionaries/rdf_dialogueevent_dictionary.txt"),
        ("rdf_voicetype_dictionary.txt",      "rdf/Dictionaries/rdf_voicetype_dictionary.txt"),
        ("rdf_voiceid_dictionary.txt",        "rdf/Dictionaries/rdf_voiceid_dictionary.txt"),
    };

    public static int Run(string repoSpec, string destDir = null)
    {
        var (owner, repo, branch) = ParseRepo(repoSpec);
        var dictDir = destDir ?? Path.Combine(AppContext.BaseDirectory, "dict");
        Directory.CreateDirectory(dictDir);

        Console.WriteLine($"Updating {Map.Length} dictionaries from {owner}/{repo}@{branch}");
        Console.WriteLine($"  -> {dictDir}");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("modbldr-tools");

        var results = new (string name, string status, bool ok)[Map.Length];
        try
        {
            var tasks = new Task[Map.Length];
            for (int i = 0; i < Map.Length; i++)
            {
                int idx = i;
                tasks[idx] = Task.Run(async () =>
                {
                    var (local, remote) = Map[idx];
                    var url = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{remote}";
                    try
                    {
                        var bytes = await http.GetByteArrayAsync(url);
                        var dst = Path.Combine(dictDir, local);
                        bool changed = !File.Exists(dst) || !File.ReadAllBytes(dst).AsSpan().SequenceEqual(bytes);
                        if (changed) File.WriteAllBytes(dst, bytes);
                        results[idx] = (local, changed ? $"updated ({bytes.Length:N0} B)" : "unchanged", true);
                    }
                    catch (Exception ex)
                    {
                        results[idx] = (local, "FOXDIE: " + Innermost(ex), false);
                    }
                });
            }
            Task.WaitAll(tasks);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FOXDIE: dictionary update failed: {Innermost(ex)}");
            return 1;
        }

        int updated = 0, unchanged = 0, failed = 0;
        foreach (var (name, status, ok) in results)
        {
            Console.WriteLine($"  {(ok ? (status.StartsWith("updated") ? "[+]" : "[=]") : "[!]")} {name,-36} {status}");
            if (!ok) failed++;
            else if (status.StartsWith("updated")) updated++;
            else unchanged++;
        }
        Console.WriteLine($"Done: {updated} updated, {unchanged} unchanged, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static (string owner, string repo, string branch) ParseRepo(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return Split(DefaultRepo, DefaultBranch);

        spec = spec.Trim();
        // Full GitHub URL form: https://github.com/<owner>/<repo>[/tree/<branch>]
        if (spec.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var after = spec[(spec.IndexOf("github.com", StringComparison.OrdinalIgnoreCase) + "github.com".Length)..]
                        .TrimStart('/');
            var parts = after.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string owner = parts.Length > 0 ? parts[0] : "";
            string repo = parts.Length > 1 ? parts[1].Replace(".git", "") : "";
            string branch = DefaultBranch;
            int t = Array.FindIndex(parts, p => p is "tree" or "blob");
            if (t >= 0 && t + 1 < parts.Length) branch = parts[t + 1];
            if (owner.Length == 0 || repo.Length == 0) return Split(DefaultRepo, DefaultBranch);
            return (owner, repo, branch);
        }
        // owner/repo[@branch] form
        return Split(spec, DefaultBranch);
    }

    private static (string, string, string) Split(string ownerRepo, string defBranch)
    {
        var branch = defBranch;
        var at = ownerRepo.IndexOf('@');
        if (at >= 0) { branch = ownerRepo[(at + 1)..]; ownerRepo = ownerRepo[..at]; }
        var seg = ownerRepo.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return seg.Length >= 2 ? (seg[0], seg[1], branch) : ("kapuragu", "mgsv-lookup-strings", branch);
    }

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException != null) ex = ex.InnerException;
        return ex.Message;
    }
}
