// Based on MtarTool/Program.cs
using System.Text;
using System.Xml.Serialization;
using MgsvModBldr.Tools.Mtar.Common;
using MgsvModBldr.Tools.Mtar.Mtar;

namespace MgsvModBldr.Tools.Mtar;

public static class MtarConverter
{
    private static XmlSerializer Serializer() =>
        new XmlSerializer(typeof(ArchiveFile), new[] { typeof(MtarFile), typeof(MtarFile2) });

    public static int GetMtarType(string path)
    {
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = new BinaryReader(input, Encoding.Default, true);
        input.Position = 0x28;
        uint offset = reader.ReadUInt32();
        reader.BaseStream.Position = offset;
        return reader.ReadUInt32() == 0xBFCA2D2 ? 1 : 2;
    }

    public static string Unpack(string mtarPath, bool numberNames = false)
    {
        // Vendored Export/Import build paths by string concat — a relative
        // input makes GetDirectoryName()="" and roots output at the drive.
        mtarPath = Path.GetFullPath(mtarPath);
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

    public static string Pack(string xmlPath)
    {
        xmlPath = Path.GetFullPath(xmlPath); // see Unpack
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
