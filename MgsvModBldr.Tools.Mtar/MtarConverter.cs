// Based on MtarTool/Program.cs
using System.Text;
using System.Xml.Serialization;
using MgsvModBldr.Tools.Mtar.Common;
using MgsvModBldr.Tools.Mtar.Mtar;

namespace MgsvModBldr.Tools.Mtar;

/// <summary>
/// Façade for the Fox Engine motion archive (.mtar) format. Mirrors
/// Atvaark's MtarTool: detects v1 vs v2 from the magic at the first
/// entry's data, extracts the contained files into a
/// <c>&lt;stem&gt;_mtar/</c> folder, and serialises the manifest XML.
/// Reuses Core.CityHash64 for the NameResolver. CLI:
/// <c>.mtar</c> -> <c>&lt;name&gt;.mtar.xml</c> + <c>&lt;stem&gt;_mtar/</c>;
/// <c>.mtar.xml</c> -> <c>&lt;name&gt;.mtar</c>.
/// </summary>
public static class MtarConverter
{
    private static XmlSerializer Serializer() =>
        new XmlSerializer(typeof(ArchiveFile), new[] { typeof(MtarFile), typeof(MtarFile2) });

    /// <summary>1 = Mtar type 1, 2 = Mtar type 2.</summary>
    public static int GetMtarType(string path)
    {
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = new BinaryReader(input, Encoding.Default, true);
        input.Position = 0x28;
        uint offset = reader.ReadUInt32();
        reader.BaseStream.Position = offset;
        return reader.ReadUInt32() == 0xBFCA2D2 ? 1 : 2;
    }

    /// <summary>Decompile a .mtar to <c>&lt;name&gt;.mtar.xml</c> + a <c>&lt;stem&gt;_mtar/</c> folder. Returns the xml path.</summary>
    public static string Unpack(string mtarPath, bool numberNames = false)
    {
        var directory = Path.GetDirectoryName(mtarPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(mtarPath);
        var ext = Path.GetExtension(mtarPath).Substring(1);
        var outputPath = directory + Path.DirectorySeparatorChar + stem + "_" + ext + Path.DirectorySeparatorChar;
        var xmlOutputPath = mtarPath + ".xml";

        ArchiveFile file = GetMtarType(mtarPath) == 1 ? new MtarFile() : new MtarFile2();
        file.numberNames = numberNames;
        file.name = Path.GetFileName(mtarPath);

        using (var input = File.Open(mtarPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var xmlOutput = File.Open(xmlOutputPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Read(input);
            file.Export(input, outputPath);
            Serializer().Serialize(xmlOutput, file);
        }
        return xmlOutputPath;
    }

    /// <summary>Recompile a <c>&lt;name&gt;.mtar.xml</c> (+ its <c>&lt;stem&gt;_mtar/</c> folder) back to <c>&lt;name&gt;.mtar</c>. Returns the mtar path.</summary>
    public static string Pack(string xmlPath)
    {
        var outputPath = xmlPath.Substring(0, xmlPath.Length - ".xml".Length);

        using (var xmlInput = File.Open(xmlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = File.Open(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var archiveFile = Serializer().Deserialize(xmlInput) as ArchiveFile;
            archiveFile.Import(output, outputPath);
        }
        return outputPath;
    }
}
