// Based on LangTool/Program.cs
using System.Text;
using System.Xml.Serialization;
using MgsvModBldr.Tools.Translation.Lang;

namespace MgsvModBldr.Tools.Translation;

/// <summary>
/// Façade for the Fox Engine language (.lng/.lng2) format. Mirrors
/// Atvaark's LangTool: the langId dictionary is loaded from
/// <c>lang_dictionary.txt</c> next to the executable, and the XML is
/// read/written through a UTF-8 StreamReader/Writer (so the declaration +
/// BOM match the reference). CLI: <c>.lng</c> -> <c>&lt;name&gt;.lng.xml</c>;
/// <c>.lng.xml</c> -> <c>&lt;name&gt;.lng</c>.
/// </summary>
public static class LangConverter
{
    private const string DictName = "lang_dictionary.txt";

    /// <summary>Build langIdCode→string lookup from the loose dictionary (if present).</summary>
    public static Dictionary<uint, string> LoadDictionary()
    {
        var dict = new Dictionary<uint, string>();
        var path = Path.Combine(AppContext.BaseDirectory, DictName);
        if (File.Exists(path))
            foreach (var value in File.ReadAllLines(path))
                dict[Fox.GetStrCode32(value)] = value;
        return dict;
    }

    /// <summary>Decompile a .lng/.lng2 to <c>&lt;name&gt;.lng.xml</c>. Returns the xml path.</summary>
    public static string Unpack(string lngPath, Dictionary<uint, string> dict = null)
    {
        dict ??= LoadDictionary();
        var outPath = lngPath + ".xml";

        LangFile file;
        using (var inputStream = File.Open(lngPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            file = LangFile.ReadLangFile(inputStream, dict);

        var serializer = new XmlSerializer(typeof(LangFile));
        using (var outputStream = File.Open(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var xmlWriter = new StreamWriter(outputStream, Encoding.UTF8))
            serializer.Serialize(xmlWriter, file);
        return outPath;
    }

    /// <summary>Recompile a <c>&lt;name&gt;.lng.xml</c> back to <c>&lt;name&gt;.lng</c>. Returns the lng path.</summary>
    public static string Pack(string xmlPath)
    {
        var outPath = xmlPath.Substring(0, xmlPath.Length - ".xml".Length);

        LangFile file;
        var serializer = new XmlSerializer(typeof(LangFile));
        using (var inputStream = File.Open(xmlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var xmlReader = new StreamReader(inputStream, Encoding.UTF8))
            file = serializer.Deserialize(xmlReader) as LangFile;

        using (var outputStream = File.Open(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
            file.Write(outputStream);
        return outPath;
    }
}
