// Based on TwpfTool/Program.cs
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Twpf;

public static class TwpfConverter
{
    private const string DictName = "twpf_stringId_dictionary.txt";

    public static bool Verbose
    {
        get => TwpfLog.IsVerbose;
        set => TwpfLog.IsVerbose = value;
    }

    private static string ResolveDict(string name)
    {
        var inDict = Path.Combine(AppContext.BaseDirectory, "dict", name);
        return File.Exists(inDict) ? inDict : Path.Combine(AppContext.BaseDirectory, name);
    }

    public static Dictionary<ulong, string> LoadDictionary()
    {
        var dict = new Dictionary<ulong, string>();
        var path = ResolveDict(DictName);
        if (File.Exists(path))
            foreach (var key in File.ReadAllLines(path))
                dict[TwpParamKeyStringId.StrCode(key)] = key;
        return dict;
    }

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
