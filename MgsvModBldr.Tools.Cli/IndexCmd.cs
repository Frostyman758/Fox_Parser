// index / search verbs: game-path view of an archive
using MgsvModBldr.Tools.Streaming;

namespace MgsvModBldr.Tools.Cli;

// index  <archive> [substring]   the archive laid out by GAME path, as a tree
// search <archive> <pattern>     the same view, filtered (* and ? glob)
//
// Both read container INDEXES only — no pack payload is decoded — so a chunk
// archive with thousands of packs costs a couple of MB instead of decoding all
// of them. Packs are flattened away: a model inside plparts_*.fpk shows up where
// the GAME looks for it, with the owning pack noted on the right.
internal static class IndexCmd
{
    public static int Run(string[] args)
    {
        bool search = args[0] is "search" or "find";
        if (args.Length < 2 || (search && args.Length < 3))
        {
            Console.Error.WriteLine(search
                ? "usage: search <archive.dat|.g0s> <pattern>     (* and ? glob)"
                : "usage: index <archive.dat|.g0s> [substring]");
            return 2;
        }
        var archive = args[1];
        if (!File.Exists(archive)) { Console.Error.WriteLine($"FOXDIE: no such archive: {archive}"); return 2; }
        var pattern = args.Length > 2 ? args[2] : null;

        VirtualListing.Result r;
        try { r = VirtualListing.Build(archive); }
        catch (Exception ex) { Console.Error.WriteLine($"FOXDIE: {ex.Message}"); return 1; }

        var matched = Filter(r.Items, pattern, search).ToList();
        if (matched.Count == 0)
        {
            Console.Error.WriteLine($"no match in {r.Items.Count:N0} entries.");
            return 1;
        }

        PrintTree(matched);
        Console.Error.WriteLine($"{matched.Count:N0} of {r.Items.Count:N0} entries · "
                              + $"{r.ContainersIndexed:N0} containers indexed · {r.IndexBytes / 1024:N0} KB read");
        return 0;
    }

    private static IEnumerable<VirtualListing.Item> Filter(
        List<VirtualListing.Item> items, string pattern, bool search)
    {
        if (pattern is null) return items;

        if (search && (pattern.Contains('*') || pattern.Contains('?')))
        {
            var rx = new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                          .Replace("\\*", ".*").Replace("\\?", ".") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return items.Where(i => rx.IsMatch(i.VirtualPath)
                                 || rx.IsMatch(Path.GetFileName(i.VirtualPath)));
        }
        return items.Where(i => i.VirtualPath.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    // Render the matched paths as a directory tree. Folders that contain exactly one
    // child folder are COLLAPSED onto one line (Assets/tpp/chara/sna/Scenes) — Fox
    // paths are deep and narrow, and a rung-per-segment tree is mostly indentation.
    private static void PrintTree(List<VirtualListing.Item> items)
    {
        var root = new Node("");
        foreach (var it in items.OrderBy(i => i.VirtualPath, StringComparer.OrdinalIgnoreCase))
        {
            var parts = it.VirtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var cur = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!cur.Dirs.TryGetValue(parts[i], out var next))
                    cur.Dirs[parts[i]] = next = new Node(parts[i]);
                cur = next;
            }
            cur.Files.Add((parts[^1], it));
        }
        Walk(root, "", true, isRoot: true);
    }

    private static void Walk(Node n, string prefix, bool last, bool isRoot)
    {
        if (!isRoot)
        {
            // Collapse a chain of single-child folders into one label.
            var label = n.Name;
            var cur = n;
            while (cur.Files.Count == 0 && cur.Dirs.Count == 1)
            {
                cur = cur.Dirs.Values.First();
                label += "/" + cur.Name;
            }
            Console.WriteLine($"{prefix}{(last ? "└─ " : "├─ ")}{label}/");
            prefix += last ? "   " : "│  ";
            n = cur;
        }

        var dirs = n.Dirs.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var files = n.Files.OrderBy(f => f.name, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < dirs.Count; i++)
            Walk(dirs[i], prefix, i == dirs.Count - 1 && files.Count == 0, isRoot: false);
        for (int i = 0; i < files.Count; i++)
        {
            var (name, it) = files[i];
            var tee = i == files.Count - 1 ? "└─ " : "├─ ";
            var pack = it.InPack ? $"   ← {LeafOf(it.Pack)}" : "";
            Console.WriteLine($"{prefix}{tee}{name}  ({it.Size:N0}){pack}");
        }
    }

    private static string LeafOf(string p)
    {
        var s = p.Replace('\\', '/').TrimEnd('/');
        int i = s.LastIndexOf('/');
        return i < 0 ? s : s[(i + 1)..];
    }

    private sealed class Node
    {
        public readonly string Name;
        public readonly SortedDictionary<string, Node> Dirs = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string name, VirtualListing.Item it)> Files = new();
        public Node(string name) => Name = name;
    }
}
