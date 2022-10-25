using System.Collections.Generic;
using System.IO;
using System.Text;

using Syroot.BinaryData;
using Syroot.BinaryData.Core;
using Syroot.BinaryData.Memory;

using GT.Shared.Polyphony;
using GT4FS.Core.Entries;

namespace GT4FS.Core
{
    public class ToCPage
    {
        public short Flag { get; set; }
        public short EntryCount { get; set; }
        public int NextPage { get; set; }
        public int PreviousPage { get; set; }
        public List<Entry> NodeEntries { get; set; }

        private readonly byte[] _pageData;

        public ToCPage(byte[] data)
        {
            _pageData = data;
            NodeEntries = new List<Entry>();
            Read();
        }

        public void Read()
        {
            SpanReader reader = new SpanReader(_pageData);

            Flag = reader.ReadInt16();
            EntryCount = reader.ReadInt16();
            NextPage = reader.ReadInt32();
            PreviousPage = reader.ReadInt32();

            if (Flag != 0)
                return;

            int pageEndPos = _pageData.Length;
            int realEntryCount = EntryCount / 2;
            for (var i = 0; i < realEntryCount; i++)
            {
                reader.Position = pageEndPos - ((i * 0x08) + 0x08);

                short entryOffset = reader.ReadInt16();
                short entryLength = reader.ReadInt16();
                short entryTypeMetaOffset = reader.ReadInt16();
                short entryTypeMetaLength = reader.ReadInt16();

                reader.Position = entryOffset;

                reader.Endian = Endian.Big;
                int parentID = reader.ReadInt32();
                reader.Endian = Endian.Little;

                string name = reader.ReadStringRaw(entryLength - 4);

                reader.Position = entryTypeMetaOffset;
                var entry = Entry.ReadEntryFromBuffer(ref reader);
                entry.Name = name;
                entry.ParentNode = parentID;
                NodeEntries.Add(entry);
            }
        }
    }
}
