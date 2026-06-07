// Based on RdfTool/TPP/RadioLabelPart2.cs
using System;
using System.IO;
using System.Xml;
using System.Xml.Schema;
using System.Globalization;
using System.Collections.Generic;

namespace MgsvModBldr.Tools.Rdf
{
    public class RadioLabelPart2 : RadioGroupPart
    {
        public FnvHash Condition { get; set; }
        public override void Read(BinaryReader reader, HashManager hashManager, HashIdentifiedDelegate hashIdentifiedCallback)
        {
            Condition = new FnvHash();
            Condition.Read(reader, hashManager.Fnv1LookupTable, hashIdentifiedCallback);

            base.Read(reader, hashManager, hashIdentifiedCallback);
        }
        public override void Write(BinaryWriter writer)
        {
            Condition.Write(writer);

            base.Write(writer);

            writer.Write((byte)(IntervalNextLabelId << 4));
        }
        public void ReadXml(XmlReader reader, List<FnvHash> dialogueEvents, List<FnvHash> charas)
        {
            Condition = new FnvHash();
            Condition.ReadXml(reader, "condition");

            FnvHash dialogueEvent = new FnvHash();
            dialogueEvent.ReadXml(reader, "dialogueEvent");
            FnvHash chara = new FnvHash();
            chara.ReadXml(reader, "chara");

            bool dialogueEventDoAdd = true;
            if (reader["dialogueEvent"] == "Invalid")
            {
                DialogueEventIndex = -1;
            }
            else
            {
                foreach (FnvHash _dialogueEvent in dialogueEvents)
                    if (_dialogueEvent.HashValue == dialogueEvent.HashValue)
                    {
                        dialogueEventDoAdd = false;
                        DialogueEventIndex = (sbyte)dialogueEvents.IndexOf(_dialogueEvent);
                        break;
                    }

                if (dialogueEventDoAdd)
                {
                    dialogueEvents.Add(dialogueEvent);
                    DialogueEventIndex = (sbyte)dialogueEvents.IndexOf(dialogueEvent);
                }
            }

            bool charaDoAdd = true;
            if (reader["chara"] == "Invalid")
                CharaIndex = -1;
            else
            {
                foreach (FnvHash _chara in charas)
                    if (_chara.HashValue == chara.HashValue)
                    {
                        charaDoAdd = false;
                        CharaIndex = (sbyte)charas.IndexOf(_chara);
                        break;
                    }

                if (charaDoAdd)
                {
                    charas.Add(chara);
                    CharaIndex = (sbyte)charas.IndexOf(chara);
                }
            }

            IntervalNextLabelId = byte.Parse(reader["intervalNextLabelId"]);
            reader.ReadStartElement("labelPart");
        }

        public override void WriteXml(XmlWriter writer)
        {
            writer.WriteStartElement("labelPart");
            Condition.WriteXml(writer, "condition");
        }

        public XmlSchema GetSchema() {return null;}
    }
}
