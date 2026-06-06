// Based on SubpTool/Program.cs
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using MgsvModBldr.Tools.Translation.Subp;

namespace MgsvModBldr.Tools.Translation;

/// <summary>
/// Façade for the Fox Engine subtitle pack (.subp) format. Mirrors
/// Atvaark's SubpTool exactly: unpack writes the XmlSerializer output
/// (NewLineHandling.Entitize, Indent) byte-for-byte, pack reads it back.
///
/// In the unified toolset the companion file is named
/// <c>&lt;name&gt;.subp.xml</c> (format-suffixed, matching the .fpk.json /
/// .dat.json convention) so it is unambiguous from Fox's bare .xml. The
/// XML *content* is identical to SubpTool's <c>&lt;name&gt;.xml</c>.
/// </summary>
public static class SubpConverter
{
    static SubpConverter()
    {
        // ISO-8859-5 (rus) etc. live in the CodePages provider; Latin1
        // and UTF-8 are intrinsic but registering once is harmless.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Maps SubpTool's language switch to its encoding. Default
    /// (and -eng/-fre/-ger/-ita/-spa) is ISO-8859-1, which is also
    /// binary-safe: every byte 0x00-0xFF round-trips, so it preserves
    /// the original bytes for any language.
    /// </summary>
    public static Encoding ResolveEncoding(string languageOption)
    {
        switch (languageOption)
        {
            case "-rus":
                return Encoding.GetEncoding("ISO-8859-5");
            case "-jpn":
            case "-ara":
            case "-por":
                return Encoding.UTF8;
            case "-fre":
            case "-ger":
            case "-spa":
            case "-ita":
            case "-eng":
            default:
                return Encoding.GetEncoding("ISO-8859-1");
        }
    }

    /// <summary>Unpack a .subp to <c>&lt;name&gt;.subp.xml</c>. Returns the xml path.</summary>
    public static string Unpack(string subpPath, Encoding encoding = null)
    {
        encoding ??= ResolveEncoding("");
        var outPath = subpPath + ".xml"; // <name>.subp -> <name>.subp.xml

        using (var inputStream = new FileStream(subpPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var outputWriter = XmlWriter.Create(outPath, new XmlWriterSettings
        {
            NewLineHandling = NewLineHandling.Entitize,
            Indent = true
        }))
        {
            SubpFile subpFile = SubpFile.ReadSubpFile(inputStream, encoding);
            var serializer = new XmlSerializer(typeof(SubpFile));
            serializer.Serialize(outputWriter, subpFile);
        }
        return outPath;
    }

    /// <summary>Pack a <c>&lt;name&gt;.subp.xml</c> back to <c>&lt;name&gt;.subp</c>. Returns the subp path.</summary>
    public static string Pack(string xmlPath, Encoding encoding = null)
    {
        encoding ??= ResolveEncoding("");
        var outPath = xmlPath.Substring(0, xmlPath.Length - ".xml".Length); // strip trailing .xml

        using (var inputStream = new FileStream(xmlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var outputStream = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var serializer = new XmlSerializer(typeof(SubpFile));
            var subpFile = serializer.Deserialize(inputStream) as SubpFile;
            subpFile?.Write(outputStream, encoding);
        }
        return outPath;
    }
}
