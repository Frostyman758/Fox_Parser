// Based on SpchTool/Program.cs
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace MgsvModBldr.Tools.Spch;

/// <summary>
/// Façade for the Fox Engine speech table (.spch) format. Mirrors
/// Atvaark's SpchTool: StrCode32 (Core.CityHash64) + FNV1 hashes are
/// resolved against the spch_* dictionaries shipped next to the exe; the
/// XML is written with XmlWriter (UTF-8, indented). CLI:
/// <c>.spch</c> -> <c>&lt;name&gt;.spch.xml</c>; <c>.spch.xml</c> -> <c>&lt;name&gt;.spch</c>.
///
/// SpchTool's side files (spch_hash_dump_dictionary.txt, appends to
/// spch_user_dictionary.txt) are intentionally NOT produced — they don't
/// affect the .spch/.xml output.
/// </summary>
public static class SpchConverter
{
    private static readonly string[] StrCodeDicts =
    {
        "spch_dictionary.txt", "spch_label_dictionary.txt",
        "spch_voicetype_dictionary.txt", "spch_anim_dictionary.txt",
        "spch_user_dictionary.txt",
    };
    private static readonly string[] FnvDicts =
    {
        "spch_fnv_voiceevent_dictionary.txt", "spch_fnv_voiceid_dictionary.txt",
        "spch_user_dictionary.txt",
    };

    private static HashManager BuildHashManager()
    {
        var hm = new HashManager();
        hm.StrCode32LookupTable = BuildTable(StrCodeDicts, HashManager.StrCode32);
        hm.Fnv1LookupTable = BuildTable(FnvDicts, HashManager.FNV1Hash32Str);
        return hm;
    }

    private static string ResolveDict(string name)
    {
        var inDict = Path.Combine(AppContext.BaseDirectory, "dict", name);
        return File.Exists(inDict) ? inDict : Path.Combine(AppContext.BaseDirectory, name);
    }

    private static Dictionary<uint, string> BuildTable(string[] names, Func<string, uint> hash)
    {
        var literals = new List<string>();
        foreach (var n in names)
        {
            var path = ResolveDict(n);
            if (File.Exists(path))
                literals.AddRange(File.ReadAllLines(path));
        }
        var table = new ConcurrentDictionary<uint, string>();
        Parallel.ForEach(literals, e => table.TryAdd(hash(e), e));
        return new Dictionary<uint, string>(table);
    }

    /// <summary>Decompile a .spch to <c>&lt;name&gt;.spch.xml</c>. Returns the xml path.</summary>
    public static string Unpack(string spchPath)
    {
        var hashManager = BuildHashManager();
        var outPath = spchPath + ".xml";

        var spch = new SpchFile();
        using (var reader = new BinaryReader(File.Open(spchPath, FileMode.Open, FileAccess.Read, FileShare.Read)))
            spch.Read(reader, hashManager);

        var settings = new XmlWriterSettings { Encoding = Encoding.UTF8, Indent = true };
        using (var writer = XmlWriter.Create(outPath, settings))
            spch.WriteXml(writer);
        return outPath;
    }

    /// <summary>Recompile a <c>&lt;name&gt;.spch.xml</c> back to <c>&lt;name&gt;.spch</c>. Returns the spch path.</summary>
    public static string Pack(string xmlPath)
    {
        var outPath = xmlPath.Substring(0, xmlPath.Length - ".xml".Length);

        var settings = new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true };
        var spch = new SpchFile();
        using (var reader = XmlReader.Create(xmlPath, settings))
            spch.ReadXml(reader);

        using (var writer = new BinaryWriter(File.Open(outPath, FileMode.Create, FileAccess.Write, FileShare.None)))
            spch.Write(writer);
        return outPath;
    }
}
