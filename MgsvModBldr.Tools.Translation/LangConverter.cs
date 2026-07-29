// Based on LangTool/Program.cs
using System.Text;
using System.Xml.Serialization;
using MgsvModBldr.Tools.Translation.Lang;

namespace MgsvModBldr.Tools.Translation;

public static class LangConverter
{
    private const string DictName = "lang_dictionary.txt";

    private static string ResolveDict(string name)
    {
        var inDict = Path.Combine(AppContext.BaseDirectory, "dict", name);
        return File.Exists(inDict) ? inDict : Path.Combine(AppContext.BaseDirectory, name);
    }

    public static Dictionary<uint, string> LoadDictionary()
    {
        var dict = new Dictionary<uint, string>();
        var path = ResolveDict(DictName);
        if (File.Exists(path))
            foreach (var value in File.ReadAllLines(path))
                dict[Fox.GetStrCode32(value)] = value;
        return dict;
    }

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
