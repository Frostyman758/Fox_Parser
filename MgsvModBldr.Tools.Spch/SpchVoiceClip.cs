// Based on SpchTool/SpchVoiceClip.cs (diagnostic Console output removed)
using System.IO;
using System.Xml;
using System.Xml.Schema;
using System.Globalization;

namespace MgsvModBldr.Tools.Spch
{
    public class SpchVoiceClip
    {
        public FoxHash VoiceType { get; set; }
        public FnvHash VoiceId { get; set; }
        public FoxHash AnimationAct { get; set; }
        public float BeforePause { get; set; }
        public float AfterPause { get; set; }

        public void Read(BinaryReader reader, HashManager hashManager, HashIdentifiedDelegate hashIdentifiedCallback)
        {
            VoiceType = new FoxHash();
            VoiceType.Read(reader, hashManager.StrCode32LookupTable, hashIdentifiedCallback);
            VoiceId = new FnvHash();
            VoiceId.Read(reader, hashManager.Fnv1LookupTable, hashIdentifiedCallback);
            AnimationAct = new FoxHash();
            AnimationAct.Read(reader, hashManager.StrCode32LookupTable, hashIdentifiedCallback);
            BeforePause = reader.ReadSingle();
            AfterPause = reader.ReadSingle();
        }

        public void Write(BinaryWriter writer)
        {
            VoiceType.Write(writer);
            VoiceId.Write(writer);
            AnimationAct.Write(writer);
            writer.Write(BeforePause);
            writer.Write(AfterPause);
        }

        public void ReadXml(XmlReader reader)
        {
            VoiceType = new FoxHash();
            VoiceType.ReadXml(reader, "voiceType");
            VoiceId = new FnvHash();
            VoiceId.ReadXml(reader, "sbpVoiceClipId");
            AnimationAct = new FoxHash();
            AnimationAct.ReadXml(reader, "animationAct");
            BeforePause = Extensions.ParseFloatRoundtrip(reader["beforePause"]);
            AfterPause = Extensions.ParseFloatRoundtrip(reader["afterPause"]);
            reader.ReadStartElement("voiceClip");
        }

        public void WriteXml(XmlWriter writer)
        {
            VoiceType.WriteXml(writer, "voiceType");
            VoiceId.WriteXml(writer, "sbpVoiceClipId");
            AnimationAct.WriteXml(writer, "animationAct");
            writer.WriteAttributeString("beforePause", BeforePause.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("afterPause", AfterPause.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }

        public XmlSchema GetSchema() { return null; }
    }
}
