// Based on MtarTool.Core/Mtar/MtarFile2.cs
using MgsvModBldr.Tools.Mtar.Common;
using MgsvModBldr.Tools.Mtar.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Mtar.Mtar
{
    [XmlType("MtarFile2")]
    public class MtarFile2 : ArchiveFile
    {
        [XmlAttribute("Signature")]
        public uint signature;

        [XmlIgnore]
        public uint fileCount;

        // Header field names are the engine's own, from fox::anim::MtarHeader in the decompile.
        // UnitCount/SegmentCount mirror the common TrackHeader at CommonInfoOffset.

        /// <summary>Tracks (bones) in the common track layout. Mirrors TrackHeader.UnitCount.</summary>
        [XmlAttribute("UnitCount")]
        public ushort unitCount;

        /// <summary>Segments in the common track layout. Mirrors TrackHeader.SegmentCount.</summary>
        [XmlAttribute("SegmentCount")]
        public ushort segmentCount;

        /// <summary>Shader-animation nodes. 0 across every archive measured.</summary>
        [XmlAttribute("ShaderNodeCount")]
        public ushort shaderNodeCount;

        /// <summary>Shader-animation units. 0 across every archive measured.</summary>
        [XmlAttribute("ShaderUnitCount")]
        public ushort shaderUnitCount;

        /// <summary>Largest motion-point unit count of any clip in this archive (0 in some
        /// archives). The engine sizes per-archive motion-point storage from it, so importing
        /// clips with more units than this needs it raised — see TranscodeCmd.</summary>
        [XmlAttribute("MotionPointUnitCount")]
        public ushort motionPointUnitCount;

        /// <summary>fox::anim::MtarFlags — NEW=0x1000 selects the 32-byte MtarTableList2 entry
        /// (without it the engine reads the 16-byte v1 table), HAS_SKEL_LIST=0x4000.</summary>
        [XmlAttribute("Flags")]
        public ushort flags;

        /// <summary>Self-relative from the MotionPointUnitCount field (file offset 0x10) to the
        /// common track info — MtarFile::GetCommonTrackHeader does &MotionPointUnitCount + this.
        /// It lands on a FoxData block whose payload is the shared TrackHeader.</summary>
        [XmlIgnore]
        public uint commonInfoOffset;

        /// <summary>See MtarFile.hashFlavor — the container type does not imply the game.</summary>
        [XmlAttribute("HashFlavor")]
        public string hashFlavor = "Tpp";

        /// <summary>Emitted only for GZ — see MtarFile.hashFlavorSpecified.</summary>
        [XmlIgnore]
        public bool hashFlavorSpecified;

        [XmlArray("Entries")]
        public List<MtarGaniFile2> files = new List<MtarGaniFile2>();

        // ── CommonInfo: the node chain between the entry table and the first gani body ──
        // Order and membership vary, so the chain is listed explicitly; every node's payload is
        // fully typed. NextNodeOffset is always align16(16 + DataSize), so no padding is stored.

        /// <summary>Node names in chain order, e.g. "4fbdaaef 3b9a7784".</summary>
        [XmlAttribute("CommonInfo")]
        public string commonInfo = "";

        [XmlElement("TrackInfo")]
        public MtarTrackInfo trackInfo;

        [XmlElement("MotionPointUnits")]
        public MtarMotionPointUnits motionPointUnits;

        [XmlElement("SkeletonList")]
        public MtarSkeletonList skeletonList;

        private static int Align16(int n) => (n + 15) / 16 * 16;

        /// <summary>The track node exactly as it sits in the file — 16-byte node header followed
        /// by the payload — for callers that compare layouts (TrackLayout.FromTrk).</summary>
        public byte[] TrackNodeBytes()
        {
            if (trackInfo is null) return System.Array.Empty<byte>();
            var pay = trackInfo.Write();
            var outp = new byte[0x10 + pay.Length];
            BitConverter.GetBytes(MtarNode.TrackInfo).CopyTo(outp, 0);
            BitConverter.GetBytes(pay.Length).CopyTo(outp, 4);
            pay.CopyTo(outp, 0x10);
            return outp;
        }

        public override void Read(Stream input)
        {
            BinaryReader reader = new BinaryReader(input, Encoding.Default, true);

            signature = reader.ReadUInt32();
            fileCount = reader.ReadUInt32();
            unitCount = reader.ReadUInt16();
            segmentCount = reader.ReadUInt16();
            shaderNodeCount = reader.ReadUInt16();
            shaderUnitCount = reader.ReadUInt16();
            motionPointUnitCount = reader.ReadUInt16();
            flags = reader.ReadUInt16();
            commonInfoOffset = reader.ReadUInt32();

            reader.Skip(8);

            for (int i = 0; i < fileCount; i++)
            {
                MtarGaniFile2 mtarGaniFile2 = new MtarGaniFile2();
                mtarGaniFile2.Read(input);
                files.Add(mtarGaniFile2);
            }

            if (files.Exists(f => f.isGz)) { hashFlavor = "Gz"; hashFlavorSpecified = true; }

            // End chunks are contiguous and form the last section of the file, so each one runs
            // to the next chunk's offset and the final one to EOF. Konami does not lay them out
            // in entry order, hence the sort. Deriving the size this way replaces a scan for a
            // magic value that walked off the end of every archive we wrote.
            var chunked = files.FindAll(f => f.endChunkOffset != 0);
            chunked.Sort((x, y) => x.endChunkOffset.CompareTo(y.endChunkOffset));
            for (int i = 0; i < chunked.Count; i++)
            {
                long end = i + 1 < chunked.Count ? chunked[i + 1].endChunkOffset : input.Length;
                long size = end - chunked[i].endChunkOffset;
                chunked[i].endChunkSize = size > 0 && size <= int.MaxValue ? (int)size : 0;
            }

            // Walk the CommonInfo chain and decode each node by name.
            var raw = new byte[input.Length];
            input.Position = 0;
            int got = 0;
            while (got < raw.Length)
            {
                int r = input.Read(raw, got, raw.Length - got);
                if (r <= 0) break;
                got += r;
            }

            var order = new List<string>();
            for (long at = commonInfoOffset; at + 16 <= raw.Length; )
            {
                uint nodeName = BitConverter.ToUInt32(raw, (int)at);
                int dataSize = BitConverter.ToInt32(raw, (int)at + 4);
                int next = BitConverter.ToInt32(raw, (int)at + 8);
                int pay = (int)at + 0x10;
                if (dataSize < 0 || pay + dataSize > raw.Length) break;

                order.Add(nodeName.ToString("x8"));
                if (nodeName == MtarNode.TrackInfo) trackInfo = MtarTrackInfo.Read(raw, pay, dataSize);
                else if (nodeName == MtarNode.MotionPointUnits) motionPointUnits = MtarMotionPointUnits.Read(raw, pay, dataSize);
                else if (nodeName == MtarNode.SkeletonList) skeletonList = MtarSkeletonList.Read(raw, pay, dataSize);

                if (next == 0) break;
                at += next;
            }
            commonInfo = string.Join(" ", order);
        }

        public override void Export(Stream output, string path)
        {
            string fileName = Path.GetFileNameWithoutExtension(Path.GetDirectoryName(path).Replace("_mtar", ".mtar"));

            Directory.CreateDirectory(Path.GetDirectoryName(path + "1.trk"));

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

                Directory.CreateDirectory(Path.GetDirectoryName(path + files[i].name + ".gani"));
                File.WriteAllBytes(path + files[i].name + ".gani", files[i].ReadData(output));

                // .mtp = motion-point (root trajectory) tracks. The original tool called this
                // "exchnk"; that name describes nothing. .exchnk is still read below.
                if (files[i].motionPointsSize != 0x0)
                {
                    File.WriteAllBytes(path + files[i].name + ".mtp", files[i].ReadMotionPointData(output));
                }

                if (files[i].endChunkOffset != 0x0)
                {
                    File.WriteAllBytes(path + files[i].name + ".enchnk", files[i].ReadEndChunkData(output));
                }
            }
        }

        public override void Import(Stream output, string path)
        {
            string inputPath = Path.GetDirectoryName(path) + @"\" + Path.GetFileNameWithoutExtension(path);

            uint offset;
            BinaryWriter writer = new BinaryWriter(output, Encoding.Default, true);

            fileCount = (uint)files.Count;

            writer.Write(signature);
            writer.Write(fileCount);
            writer.Write(unitCount);
            writer.Write(segmentCount);
            writer.Write(shaderNodeCount);
            writer.Write(shaderUnitCount);
            writer.Write(motionPointUnitCount);
            writer.Write(flags);
            writer.WriteZeros(0xC);

            for (int i = 0; i < files.Count; i++)
            {
                files[i].hash = NameResolver.GetHashFromName(files[i].name, hashFlavor == "Gz");
            }

            // Two orders, and they are not the same. The TABLE must be hash-sorted because the
            // engine binary-searches it (MtarFile::GetAnimFile -> SearchGani), but the payload
            // keeps the order the entries appear in the XML — which Export wrote in offset order,
            // so it is Konami's original layout. Sorting before writing bodies relocated every
            // clip in the file; 93 of 94 moved in player2_cqc.
            var payloadOrder = new List<MtarGaniFile2>(files);

            files.Sort((x, y) => x.hash.CompareTo(y.hash));

            var tableIndex = new Dictionary<MtarGaniFile2, int>(files.Count);
            for (int i = 0; i < files.Count; i++) tableIndex[files[i]] = i;

            for (int i = 0; i < files.Count; i++)
            {
                files[i].Write(output);
            }

            // ── rebuild the CommonInfo chain ──
            offset = (uint)output.Position;
            writer.BaseStream.Position = 0x14;
            writer.Write(offset);
            writer.BaseStream.Position = offset;

            var chain = new List<(uint Name, byte[] Payload)>();
            foreach (var nm in (commonInfo ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                uint h = Convert.ToUInt32(nm, 16);
                if (h == MtarNode.TrackInfo && trackInfo is not null) chain.Add((h, trackInfo.Write()));
                else if (h == MtarNode.MotionPointUnits && motionPointUnits is not null) chain.Add((h, motionPointUnits.Write()));
                else if (h == MtarNode.SkeletonList && skeletonList is not null) chain.Add((h, skeletonList.Write()));
            }
            for (int i = 0; i < chain.Count; i++)
            {
                int span = Align16(0x10 + chain[i].Payload.Length);
                writer.Write(chain[i].Name);
                writer.Write(chain[i].Payload.Length);
                writer.Write(i + 1 < chain.Count ? span : 0);
                writer.Write(0);
                writer.Write(chain[i].Payload);
                writer.WriteZeros(span - 0x10 - chain[i].Payload.Length);
            }

            foreach (var entry in payloadOrder)
            {
                int i = tableIndex[entry];
                byte[] file = File.ReadAllBytes(inputPath + @"_mtar\" + files[i].name + ".gani");
                byte[] exFile;
                offset = (uint)output.Position;
                output.Position = (0x20 + ((0x20 * i) + 0x8));
                writer.Write(offset);
                writer.Write((ushort)(file.Length / 0x10));

                var mtpPath = inputPath + @"_mtar\" + files[i].name + ".mtp";
                if (!File.Exists(mtpPath)) mtpPath = inputPath + @"_mtar\" + files[i].name + ".exchnk";
                if (File.Exists(mtpPath))
                {
                    writer.Write((ushort)(file.Length / 0x10));
                    exFile = File.ReadAllBytes(mtpPath);
                    writer.Write((ushort)(exFile.Length / 0x10));
                }
                else
                {
                    exFile = new byte[0];
                }

                if (files[i].shaderTracksOffset != 0)
                {
                    output.Position = 0x20 + (0x20 * i) + 0x12;
                    writer.Write(files[i].shaderTracksOffset);
                }

                output.Position = offset;
                writer.Write(file);

                if (exFile.Length > 0)
                {
                    writer.Write(exFile);
                }
            }

            foreach (var entry in payloadOrder)   // end chunks follow the bodies' order
            {
                int i = tableIndex[entry];
                if (File.Exists(inputPath + @"_mtar\" + files[i].name + ".enchnk"))
                {
                    byte[] file = File.ReadAllBytes(inputPath + @"_mtar\" + files[i].name + ".enchnk");

                    offset = (uint)output.Position;
                    output.Position = (0x30 + ((0x20 * i) + 0x8));
                    writer.Write(offset);
                    output.Position = offset;
                    writer.Write(file);
                }
            }

            // Every stock mtar is a whole number of 16-byte lines; ours were not, which left the
            // final end chunk running off a ragged edge. Pad to match.
            if (output.Position % 0x10 != 0)
                writer.WriteZeros(0x10 - (int)(output.Position % 0x10));
        }
    }
}
