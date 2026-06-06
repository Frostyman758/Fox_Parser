// Based on TwpfTool/Program.cs
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Twpf;

/// <summary>
/// Façade for the Fox Engine weather-parameter (.twpf) format. Mirrors
/// Atvaark's TwpfXmlTool exactly: the StringId dictionary is loaded from
/// <c>twpf_stringId_dictionary.txt</c> next to the executable, and the
/// XML is written via <see cref="XmlSerializer"/>.Serialize(Stream) (the
/// same overload the original uses — indented, no encoding attribute).
///
/// CLI: <c>.twpf</c> -> <c>&lt;name&gt;.twpf.xml</c>; <c>.twpf.xml</c> -> <c>&lt;name&gt;.twpf</c>.
/// </summary>
public static class TwpfConverter
{
    private const string DictName = "twpf_stringId_dictionary.txt";

    public static bool Verbose
    {
        get => TwpfLog.IsVerbose;
        set => TwpfLog.IsVerbose = value;
    }

    /// <summary>Build hash→string lookup from the loose dictionary (if present).</summary>
    public static Dictionary<ulong, string> LoadDictionary()
    {
        var dict = new Dictionary<ulong, string>();
        var path = Path.Combine(AppContext.BaseDirectory, DictName);
        if (File.Exists(path))
            foreach (var key in File.ReadAllLines(path))
                dict[TwpParamKeyStringId.StrCode(key)] = key;
        return dict;
    }

    /// <summary>Decompile a .twpf to <c>&lt;name&gt;.twpf.xml</c>. Returns the xml path.</summary>
    public static string Unpack(string twpfPath, Dictionary<ulong, string> dict = null)
    {
        dict ??= LoadDictionary();
        var dir = Path.GetDirectoryName(twpfPath) ?? ".";
        var outPath = Path.Combine(dir, Path.GetFileName(twpfPath) + ".xml");

        var twpf = new TwpFile();
        using (var reader = new BinaryReader(File.Open(twpfPath, FileMode.Open, FileAccess.Read, FileShare.Read)))
            twpf.Read(reader, dict);

        var serializer = new XmlSerializer(typeof(TwpFile));
        using (var xmlStream = File.Open(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
            serializer.Serialize(xmlStream, twpf);
        return outPath;
    }

    /// <summary>Recompile a <c>&lt;name&gt;.twpf.xml</c> back to <c>&lt;name&gt;.twpf</c>. Returns the twpf path.</summary>
    public static string Pack(string xmlPath)
    {
        var outPath = xmlPath.Substring(0, xmlPath.Length - ".xml".Length);

        TwpFile file;
        var serializer = new XmlSerializer(typeof(TwpFile));
        using (var xmlStream = File.Open(xmlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            file = (TwpFile)serializer.Deserialize(xmlStream);

        using (var writer = new BinaryWriter(File.Open(outPath, FileMode.Create, FileAccess.Write, FileShare.None)))
            file.Write(writer);
        return outPath;
    }
}
