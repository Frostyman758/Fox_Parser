// Based on MtarTool.Core/Mtar/MtarFile.cs
using MgsvModBldr.Tools.Mtar.Common;
using MgsvModBldr.Tools.Mtar.Utility;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Mtar.Mtar
{
    [XmlType("MtarFile")]
    public class MtarFile : ArchiveFile
    {
        [XmlAttribute("Signature")]
        public uint signature;

        [XmlIgnore]
        public uint fileCount;

        [XmlAttribute("BoneGroups")]
        public ulong boneGroups;

        [XmlAttribute("BoneGroups2")]
        public ulong boneGroups2;

        /// <summary>
        /// Which game's hash scheme this mtar's entries use. Recorded on read and
        /// replayed on write, because the container TYPE does not imply it: TPP ships
        /// type-1 mtars that use TPP hashing, while every GZ mtar is type 1 with GZ
        /// hashing. Without this a GZ mtar unpacks fine but repacks with TPP hashes.
        /// </summary>
        [XmlAttribute("HashFlavor")]
        public string hashFlavor = "Tpp";

        /// <summary>
        /// Emit HashFlavor ONLY for GZ. TPP is the default and the reference tool
        /// writes no such attribute, so staying silent keeps TPP xml byte-identical
        /// to MtarTool's while still recording the one case it gets wrong.
        /// </summary>
        [XmlIgnore]
        public bool hashFlavorSpecified;

        [XmlArray("Entries")]
        public List<MtarGaniFile> files = new List<MtarGaniFile>();

        public override void Read(Stream input)
        {
            BinaryReader reader = new BinaryReader(input, Encoding.Default, true);

            signature = reader.ReadUInt32();
            fileCount = reader.ReadUInt32();
            boneGroups = reader.ReadUInt64();
            boneGroups2 = reader.ReadUInt64();
            reader.Skip(8);

            for (int i = 0; i < fileCount; i++)
            {
                MtarGaniFile mtarGaniFile = new MtarGaniFile();
                mtarGaniFile.Read(input);
                files.Add(mtarGaniFile);
            }

            if (files.Exists(f => f.isGz)) { hashFlavor = "Gz"; hashFlavorSpecified = true; }

            // Each body runs to the next one's offset; the tail beyond `size` is alignment padding
            // that is NOT always zero, so carry it.
            var byOffset = new List<MtarGaniFile>(files);
            byOffset.Sort((x, y) => x.offset.CompareTo(y.offset));
            for (int i = 0; i < byOffset.Count; i++)
            {
                long end = i + 1 < byOffset.Count ? byOffset[i + 1].offset : input.Length;
                long span = end - byOffset[i].offset;
                if (span > byOffset[i].size && span <= int.MaxValue)
                {
                    byOffset[i].paddedSize = (int)span;
                    byOffset[i].dataSize = byOffset[i].size;
                    byOffset[i].dataSizeSpecified = true;
                }
            }
        }

        public override void Export(Stream output, string path)
        {
            files.Sort((x, y) => x.offset.CompareTo(y.offset));

            for (int i = 0; i < files.Count; i++)
            {
                if (numberNames)
                {
                    string ganiPath = Path.GetDirectoryName(files[i].name).Replace('\\', '/');
                    string ganiName = Path.GetFileName(files[i].name);


                    if (ganiPath != "")
                    {
                        ganiPath += "/";
                    }

                    ganiPath += i.ToString("0000") + "_" + ganiName;
                    files[i].name = ganiPath;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path + files[i].name));
                File.WriteAllBytes(path + files[i].name, files[i].ReadData(output));
            }
        }

        public override void Import(Stream output, string path)
        {
            string inputPath = Path.GetDirectoryName(path) + @"\" + Path.GetFileNameWithoutExtension(path);

            uint offset = (uint)output.Position;
            BinaryWriter writer = new BinaryWriter(output, Encoding.Default, true);

            fileCount = (uint)files.Count;

            writer.Write(signature);
            writer.Write(fileCount);
            writer.Write(boneGroups);
            writer.Write(boneGroups2);
            writer.WriteZeros(8);

            for (int i = 0; i < files.Count; i++)
            {
                files[i].hash = NameResolver.GetHashFromName(files[i].name, hashFlavor == "Gz");
            }

            // Same split as v2: the TABLE is hash-sorted for the engine's binary search, but the
            // payload keeps the order the entries appear in the XML — which Export wrote in offset
            // order, so it is the original layout. Sorting before writing bodies moves every clip.
            var payloadOrder = new List<MtarGaniFile>(files);

            files.Sort((x, y) => x.hash.CompareTo(y.hash));

            for (int i = 0; i < files.Count; i++)
            {
                files[i].Write(output);
            }

            var tableIndex = new Dictionary<MtarGaniFile, int>(files.Count);
            for (int i = 0; i < files.Count; i++) tableIndex[files[i]] = i;

            foreach (var entry in payloadOrder)
            {
                int i = tableIndex[entry];
                byte[] file = File.ReadAllBytes(inputPath + @"_mtar\" + files[i].name);
                offset = (uint)writer.BaseStream.Position;
                writer.BaseStream.Position = (0x20 + ((0x10 * i) + 0x8));
                writer.Write(offset);
                writer.Write(files[i].dataSizeSpecified ? files[i].dataSize : file.Length);
                writer.BaseStream.Position = offset;
                writer.Write(file);
                output.AlignWrite(16, 0x00);
            }
        }
    }
}
