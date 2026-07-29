// Based on RdfTool/Program.cs
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace MgsvModBldr.Tools.Rdf;

public static class RdfConverter
{
    private static readonly string[] StrCodeDicts =
    {
        "rdf_label_dictionary.txt", "rdf_optionalset_dictionary.txt", "rdf_user_dictionary.txt",
    };
    private static readonly string[] FnvDicts =
    {
        "rdf_dialogueevent_dictionary.txt", "rdf_voicetype_dictionary.txt",
        "rdf_voiceid_dictionary.txt", "rdf_user_dictionary.txt",
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
            if (File.Exists(path)) literals.AddRange(File.ReadAllLines(path));
        }
        var table = new ConcurrentDictionary<uint, string>();
        Parallel.ForEach(literals, e => table.TryAdd(hash(e), e));
        return new Dictionary<uint, string>(table);
    }

    public static string Unpack(string rdfPath)
    {
        var hm = BuildHashManager();
        var outPath = rdfPath + ".xml";

        using var reader = new BinaryReader(File.Open(rdfPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        Version version = (Version)reader.ReadByte();

        var settings = new XmlWriterSettings { Encoding = Encoding.UTF8, Indent = true };
        using var writer = XmlWriter.Create(outPath, settings);
        if (version == Version.GZ)
        {
            var rdf = new RadioData();
            rdf.Read(reader, hm);
            rdf.WriteXml(writer);
        }
        else if (version == Version.TPP)
        {
            var rdf = new RadioData2();
            rdf.Read(reader, hm);
            rdf.WriteXml(writer);
        }
        else throw new ArgumentOutOfRangeException($"Unknown rdf version {(byte)version}");
        return outPath;
    }

    public static string Pack(string xmlPath)
    {
        var outPath = xmlPath.Substring(0, xmlPath.Length - ".xml".Length);

        var settings = new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true };
        using var reader = XmlReader.Create(xmlPath, settings);
        reader.Read();
        reader.Read();
        Version version = (Version)Enum.Parse(typeof(Version), reader["version"]);

        using var writer = new BinaryWriter(File.Open(outPath, FileMode.Create, FileAccess.Write, FileShare.None));
        if (version == Version.GZ)
        {
            var rdf = new RadioData();
            rdf.ReadXml(reader);
            rdf.Write(writer);
        }
        else if (version == Version.TPP)
        {
            var rdf = new RadioData2();
            rdf.ReadXml(reader);
            rdf.Write(writer);
        }
        else throw new ArgumentOutOfRangeException($"Unknown rdf version {(byte)version}");
        return outPath;
    }
}
