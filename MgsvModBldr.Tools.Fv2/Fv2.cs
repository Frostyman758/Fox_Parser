// .fv2 format lib (Read + Write)
// based on FvTwool/Fv2.cs; WinForms UI, lossy Fv2String edit path and
// MessageBox/swallow handlers removed (rethrow instead).
using System;
using System.IO;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Fv2
{
    public class Fv2
    {
        public struct TextureSwapEntry
        {
            public uint materialInstanceStrCode32 { get; set; }
            public uint textureTypeStrCode32 { get; set; }
            public short textureIndex { get; set; }
            public short materialParameterIndex { get; set; }
        }

        public struct BoneModelAttachEntry
        {
            public short fmdlIndex { get; set; }
            public short frdvIndex { get; set; }
            public short unknownIndex0 { get; set; }
            public short unknownIndex1 { get; set; }
            public short simIndex { get; set; }
            public short unknownIndex2 { get; set; }
        }

        public struct CnpModelAttachEntry
        {
            public uint cnpStrCode32 { get; set; }
            public uint emptyStrCode32 { get; set; }
            public short fmdlIndex { get; set; }
            public short frdvIndex { get; set; }
            public short unknownIndex0 { get; set; }
            public short unknownIndex1 { get; set; }
            public short simIndex { get; set; }
            public short unknownIndex2 { get; set; }
        }

        public struct VariableDataEntry
        {
            public byte typeEnum { get; set; }
            public byte unknown0 { get; set; }
            public byte subEntryCount { get; set; }
            public byte meshGroupCount { get; set; }
            public byte textureSwapCount { get; set; }
            public byte unknown1 { get; set; }
            public byte boneModelAttachmentCount { get; set; }
            public byte cnpModelAttachmentCount { get; set; }
            [XmlIgnore] public uint offset { get; set; } // recomputed by Write

            public VariableDataSubEntry[] variableDataSubEntries { get; set; }
        }

        public struct VariableDataSubEntry
        {
            public uint[] meshGroupEntries { get; set; }
            public TextureSwapEntry[] textureSwapEntries { get; set; }
            public BoneModelAttachEntry[] boneModelAttachEntries { get; set; }
            public CnpModelAttachEntry[] cnpModelAttachEntries { get; set; }
        }

        // Class Vars. Signature/offsets/counts and the global unknown0 are
        // recomputed (or forced) by Write, so they're [XmlIgnore]d — the XML
        // holds only real data, and serialised layout fields don't create
        // spurious diffs. textureCount IS written from the field (real data).
        [XmlIgnore] public ulong signature { get; set; }
        [XmlIgnore] public ushort variableDataSectionOffset { get; set; }
        [XmlIgnore] public ushort externalFileSectionOffset { get; set; }
        [XmlIgnore] public ushort variableDataSectionCount { get; set; }
        [XmlIgnore] public ushort externalFileSectionCount { get; set; }
        [XmlIgnore] public uint materialParameterSectionOffset { get; set; }
        [XmlIgnore] public uint materialParameterSectionCount { get; set; }
        public ushort textureCount { get; set; }
        [XmlIgnore] public byte hideMeshGroupCount { get; set; }
        [XmlIgnore] public byte showMeshGroupCount { get; set; }
        [XmlIgnore] public byte textureSwapCount { get; set; }
        [XmlIgnore] public byte unknown0 { get; set; }
        [XmlIgnore] public byte boneModelAttachmentCount { get; set; }
        [XmlIgnore] public byte cnpModelAttachmentCount { get; set; }

        public uint[] hideMeshGroupEntries { get; set; }
        public uint[] showMeshGroupEntries { get; set; }
        public TextureSwapEntry[] textureSwapEntries { get; set; }
        public BoneModelAttachEntry[] boneModelAttachEntries { get; set; }
        public CnpModelAttachEntry[] cnpModelAttachEntries { get; set; }
        public VariableDataEntry[] variableDataEntries { get; set; }
        public Vector4[] materialParameterEntries { get; set; }
        public ulong[] externalFileEntries { get; set; }

        public void Read(string filePath)
        {
            using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                BinaryReader reader = new BinaryReader(stream);

                signature = reader.ReadUInt64();
                variableDataSectionOffset = reader.ReadUInt16();
                externalFileSectionOffset = reader.ReadUInt16();
                variableDataSectionCount = reader.ReadUInt16();
                externalFileSectionCount = reader.ReadUInt16();
                materialParameterSectionOffset = reader.ReadUInt32();
                materialParameterSectionCount = reader.ReadUInt32();
                textureCount = reader.ReadUInt16();
                reader.BaseStream.Position += 6;
                hideMeshGroupCount = reader.ReadByte();
                showMeshGroupCount = reader.ReadByte();
                textureSwapCount = reader.ReadByte();
                unknown0 = reader.ReadByte();
                boneModelAttachmentCount = reader.ReadByte();
                cnpModelAttachmentCount = reader.ReadByte();
                reader.BaseStream.Position += 2;

                variableDataEntries = new VariableDataEntry[variableDataSectionCount];
                materialParameterEntries = new Vector4[materialParameterSectionCount];
                externalFileEntries = new ulong[externalFileSectionCount];
                hideMeshGroupEntries = new uint[hideMeshGroupCount];
                showMeshGroupEntries = new uint[showMeshGroupCount];
                textureSwapEntries = new TextureSwapEntry[textureSwapCount];
                boneModelAttachEntries = new BoneModelAttachEntry[boneModelAttachmentCount];
                cnpModelAttachEntries = new CnpModelAttachEntry[cnpModelAttachmentCount];

                for (int i = 0; i < hideMeshGroupCount; i++)
                    hideMeshGroupEntries[i] = reader.ReadUInt32();

                for (int i = 0; i < showMeshGroupCount; i++)
                    showMeshGroupEntries[i] = reader.ReadUInt32();

                for (int i = 0; i < textureSwapCount; i++)
                    textureSwapEntries[i].materialInstanceStrCode32 = reader.ReadUInt32();
                for (int i = 0; i < textureSwapCount; i++)
                    textureSwapEntries[i].textureTypeStrCode32 = reader.ReadUInt32();
                for (int i = 0; i < textureSwapCount; i++)
                    textureSwapEntries[i].textureIndex = reader.ReadInt16();
                for (int i = 0; i < textureSwapCount; i++)
                    textureSwapEntries[i].materialParameterIndex = reader.ReadInt16();

                for (int i = 0; i < boneModelAttachmentCount; i++)
                {
                    boneModelAttachEntries[i].fmdlIndex = reader.ReadInt16();
                    boneModelAttachEntries[i].frdvIndex = reader.ReadInt16();
                    boneModelAttachEntries[i].unknownIndex0 = reader.ReadInt16();
                    boneModelAttachEntries[i].unknownIndex1 = reader.ReadInt16();
                    boneModelAttachEntries[i].simIndex = reader.ReadInt16();
                    boneModelAttachEntries[i].unknownIndex2 = reader.ReadInt16();
                }

                for (int i = 0; i < cnpModelAttachmentCount; i++)
                {
                    cnpModelAttachEntries[i].cnpStrCode32 = reader.ReadUInt32();
                    cnpModelAttachEntries[i].emptyStrCode32 = reader.ReadUInt32();
                    cnpModelAttachEntries[i].fmdlIndex = reader.ReadInt16();
                    cnpModelAttachEntries[i].frdvIndex = reader.ReadInt16();
                    cnpModelAttachEntries[i].unknownIndex0 = reader.ReadInt16();
                    cnpModelAttachEntries[i].unknownIndex1 = reader.ReadInt16();
                    cnpModelAttachEntries[i].simIndex = reader.ReadInt16();
                    cnpModelAttachEntries[i].unknownIndex2 = reader.ReadInt16();
                }

                for(int i = 0; i < variableDataSectionCount; i++)
                {
                    reader.BaseStream.Position = variableDataSectionOffset + 0x10 * i;

                    variableDataEntries[i].typeEnum = reader.ReadByte();
                    variableDataEntries[i].unknown0 = reader.ReadByte();
                    variableDataEntries[i].subEntryCount = reader.ReadByte();
                    variableDataEntries[i].meshGroupCount = reader.ReadByte();
                    variableDataEntries[i].textureSwapCount = reader.ReadByte();
                    variableDataEntries[i].unknown1 = reader.ReadByte();
                    variableDataEntries[i].boneModelAttachmentCount = reader.ReadByte();
                    variableDataEntries[i].cnpModelAttachmentCount = reader.ReadByte();
                    reader.BaseStream.Position += 4;
                    variableDataEntries[i].offset = reader.ReadUInt32();

                    variableDataEntries[i].variableDataSubEntries = new VariableDataSubEntry[variableDataEntries[i].subEntryCount];
                    reader.BaseStream.Position = variableDataEntries[i].offset;

                    for(int j = 0; j < variableDataEntries[i].subEntryCount; j++)
                    {
                        variableDataEntries[i].variableDataSubEntries[j].meshGroupEntries = new uint[variableDataEntries[i].meshGroupCount];
                        variableDataEntries[i].variableDataSubEntries[j].textureSwapEntries = new TextureSwapEntry[variableDataEntries[i].textureSwapCount];
                        variableDataEntries[i].variableDataSubEntries[j].boneModelAttachEntries = new BoneModelAttachEntry[variableDataEntries[i].boneModelAttachmentCount];
                        variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries = new CnpModelAttachEntry[variableDataEntries[i].cnpModelAttachmentCount];

                        for (int k = 0; k < variableDataEntries[i].meshGroupCount; k++)
                            variableDataEntries[i].variableDataSubEntries[j].meshGroupEntries[k] = reader.ReadUInt32();

                        for (int k = 0; k < variableDataEntries[i].textureSwapCount; k++)
                            variableDataEntries[i].variableDataSubEntries[j].textureSwapEntries[k].materialInstanceStrCode32 = reader.ReadUInt32();
                        for (int k = 0; k < variableDataEntries[i].textureSwapCount; k++)
                            variableDataEntries[i].variableDataSubEntries[j].textureSwapEntries[k].textureTypeStrCode32 = reader.ReadUInt32();
                        for (int k = 0; k < variableDataEntries[i].textureSwapCount; k++)
                            variableDataEntries[i].variableDataSubEntries[j].textureSwapEntries[k].textureIndex = reader.ReadInt16();
                        for (int k = 0; k < variableDataEntries[i].textureSwapCount; k++)
                            variableDataEntries[i].variableDataSubEntries[j].textureSwapEntries[k].materialParameterIndex = reader.ReadInt16();

                        for (int k = 0; k < variableDataEntries[i].boneModelAttachmentCount; k++)
                        {
                            variableDataEntries[i].variableDataSubEntries[j].boneModelAttachEntries[k].fmdlIndex = reader.ReadInt16();
                            variableDataEntries[i].variableDataSubEntries[j].boneModelAttachEntries[k].frdvIndex = reader.ReadInt16();
                            variableDataEntries[i].variableDataSubEntries[j].boneModelAttachEntries[k].unknownIndex0 = reader.ReadInt16();
                            variableDataEntries[i].variableDataSubEntries[j].boneModelAttachEntries[k].unknownIndex1 = reader.ReadInt16();
                            variableDataEntries[i].variableDataSubEntries[j].boneModelAttachEntries[k].simIndex = reader.ReadInt16();
                            variableDataEntries[i].variableDataSubEntries[j].boneModelAttachEntries[k].unknownIndex2 = reader.ReadInt16();
                        }

                        for (int k = 0; k < variableDataEntries[i].cnpModelAttachmentCount; k++)
                        {
                            variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries[k].cnpStrCode32 = reader.ReadUInt32();
                            variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries[k].emptyStrCode32 = reader.ReadUInt32();
                            variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries[k].fmdlIndex = reader.ReadInt16();
                            variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries[k].frdvIndex = reader.ReadInt16();
                            variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries[k].unknownIndex0 = reader.ReadInt16();
                            variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries[k].unknownIndex1 = reader.ReadInt16();
                            variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries[k].simIndex = reader.ReadInt16();
                            variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries[k].unknownIndex2 = reader.ReadInt16();
                        }
                    }
                }

                reader.BaseStream.Position = materialParameterSectionOffset;

                for (int i = 0; i < materialParameterSectionCount; i++)
                {
                    materialParameterEntries[i] = new Vector4();

                    for (int j = 0; j < 4; j++)
                        materialParameterEntries[i][j] = reader.ReadSingle();
                }

                reader.BaseStream.Position = externalFileSectionOffset;

                for (int i = 0; i < externalFileSectionCount; i++)
                    externalFileEntries[i] = reader.ReadUInt64();
            }
        }

        public void Write(string filePath)
        {
            signature = 0x016E697732564F46;
            variableDataSectionCount = (ushort)variableDataEntries.Length;
            externalFileSectionCount = (ushort)externalFileEntries.Length;
            materialParameterSectionCount = (uint)materialParameterEntries.Length;
            hideMeshGroupCount = (byte)hideMeshGroupEntries.Length;
            showMeshGroupCount = (byte)showMeshGroupEntries.Length;
            textureSwapCount = (byte)textureSwapEntries.Length;
            unknown0 = 0;
            boneModelAttachmentCount = (byte)boneModelAttachEntries.Length;
            cnpModelAttachmentCount = (byte)cnpModelAttachEntries.Length;

            using (FileStream stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                BinaryWriter writer = new BinaryWriter(stream);

                writer.Write(signature);
                writer.WriteZeroes(4);
                writer.Write(variableDataSectionCount);
                writer.Write(externalFileSectionCount);
                writer.WriteZeroes(4);
                writer.Write(materialParameterSectionCount);
                writer.Write(textureCount);
                writer.WriteZeroes(6);
                writer.Write(hideMeshGroupCount);
                writer.Write(showMeshGroupCount);
                writer.Write(textureSwapCount);
                writer.Write(unknown0);
                writer.Write(boneModelAttachmentCount);
                writer.Write(cnpModelAttachmentCount);
                writer.WriteZeroes(2);

                for (int i = 0; i < hideMeshGroupCount; i++)
                    writer.Write(hideMeshGroupEntries[i]);

                for (int i = 0; i < showMeshGroupCount; i++)
                    writer.Write(showMeshGroupEntries[i]);

                for(int i = 0; i < textureSwapCount; i++)
                    writer.Write(textureSwapEntries[i].materialInstanceStrCode32);

                for (int i = 0; i < textureSwapCount; i++)
                    writer.Write(textureSwapEntries[i].textureTypeStrCode32);

                for (int i = 0; i < textureSwapCount; i++)
                    writer.Write(textureSwapEntries[i].textureIndex);

                for (int i = 0; i < textureSwapCount; i++)
                    writer.Write(textureSwapEntries[i].materialParameterIndex);

                for (int i = 0; i < boneModelAttachmentCount; i++)
                {
                    writer.Write(boneModelAttachEntries[i].fmdlIndex);
                    writer.Write(boneModelAttachEntries[i].frdvIndex);
                    writer.Write(boneModelAttachEntries[i].unknownIndex0);
                    writer.Write(boneModelAttachEntries[i].unknownIndex1);
                    writer.Write(boneModelAttachEntries[i].simIndex);
                    writer.Write(boneModelAttachEntries[i].unknownIndex2);
                }

                for (int i = 0; i < cnpModelAttachmentCount; i++)
                {
                    writer.Write(cnpModelAttachEntries[i].cnpStrCode32);
                    writer.Write(cnpModelAttachEntries[i].emptyStrCode32);
                    writer.Write(cnpModelAttachEntries[i].fmdlIndex);
                    writer.Write(cnpModelAttachEntries[i].frdvIndex);
                    writer.Write(cnpModelAttachEntries[i].unknownIndex0);
                    writer.Write(cnpModelAttachEntries[i].unknownIndex1);
                    writer.Write(cnpModelAttachEntries[i].simIndex);
                    writer.Write(cnpModelAttachEntries[i].unknownIndex2);
                }

                if (writer.BaseStream.Position % 0x10 != 0)
                    writer.WriteZeroes(0x10 - (int)writer.BaseStream.Position % 0x10);

                variableDataSectionOffset = (ushort)writer.BaseStream.Position;

                for(int i = 0; i < variableDataSectionCount; i++)
                {
                    writer.Write(variableDataEntries[i].typeEnum);
                    writer.Write(variableDataEntries[i].unknown0);
                    writer.Write(variableDataEntries[i].subEntryCount);
                    writer.Write(variableDataEntries[i].meshGroupCount);
                    writer.Write(variableDataEntries[i].textureSwapCount);
                    writer.Write(variableDataEntries[i].unknown1);
                    writer.Write(variableDataEntries[i].boneModelAttachmentCount);
                    writer.Write(variableDataEntries[i].cnpModelAttachmentCount);
                    writer.WriteZeroes(8);
                }

                for (int i = 0; i < variableDataSectionCount; i++)
                {
                    variableDataEntries[i].offset = (uint)writer.BaseStream.Position;

                    for(int j = 0; j < variableDataEntries[i].subEntryCount; j++)
                    {
                        for(int k = 0; k < variableDataEntries[i].meshGroupCount; k++)
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].meshGroupEntries[k]);

                        for(int k = 0; k < variableDataEntries[i].textureSwapCount; k++)
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].textureSwapEntries[k].materialInstanceStrCode32);

                        for (int k = 0; k < variableDataEntries[i].textureSwapCount; k++)
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].textureSwapEntries[k].textureTypeStrCode32);

                        for (int k = 0; k < variableDataEntries[i].textureSwapCount; k++)
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].textureSwapEntries[k].textureIndex);

                        for (int k = 0; k < variableDataEntries[i].textureSwapCount; k++)
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].textureSwapEntries[k].materialParameterIndex);

                        for (int k = 0; k < variableDataEntries[i].boneModelAttachmentCount; k++)
                        {
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].boneModelAttachEntries[k].fmdlIndex);
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].boneModelAttachEntries[k].frdvIndex);
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].boneModelAttachEntries[k].unknownIndex0);
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].boneModelAttachEntries[k].unknownIndex1);
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].boneModelAttachEntries[k].simIndex);
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].boneModelAttachEntries[k].unknownIndex2);
                        }

                        for (int k = 0; k < variableDataEntries[i].cnpModelAttachmentCount; k++)
                        {
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries[k].cnpStrCode32);
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries[k].emptyStrCode32);
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries[k].fmdlIndex);
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries[k].frdvIndex);
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries[k].unknownIndex0);
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries[k].unknownIndex1);
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries[k].simIndex);
                            writer.Write(variableDataEntries[i].variableDataSubEntries[j].cnpModelAttachEntries[k].unknownIndex2);
                        }
                    }
                }

                if (writer.BaseStream.Position % 0x10 != 0)
                    writer.WriteZeroes(0x10 - (int)writer.BaseStream.Position % 0x10);

                materialParameterSectionOffset = (ushort)writer.BaseStream.Position;

                for (int i = 0; i < materialParameterSectionCount; i++)
                    for (int j = 0; j < 4; j++)
                        writer.Write(materialParameterEntries[i][j]);

                externalFileSectionOffset = (ushort)writer.BaseStream.Position;

                for (int i = 0; i < externalFileSectionCount; i++)
                    writer.Write(externalFileEntries[i]);

                //Offset writing time!
                writer.BaseStream.Position = 8;
                writer.Write(variableDataSectionOffset);
                writer.Write(externalFileSectionOffset);

                writer.BaseStream.Position = 0x10;
                writer.Write(materialParameterSectionOffset);

                for (int i = 0; i < variableDataSectionCount; i++)
                {
                    writer.BaseStream.Position = variableDataSectionOffset + 0x10 * i + 0xC;
                    writer.Write(variableDataEntries[i].offset);
                }
            }
        }
    }
}
