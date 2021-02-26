using System.Collections.Generic;
using System.IO;
using System.Text;

using Syroot.BinaryData;
using Syroot.BinaryData.Memory;

using GT.Shared.Polyphony;
using GT4FS.Core.Entries;

namespace GT4FS.Core
{
    public class Node
    {
        public short Flag { get; set; }
        public short EntryCount { get; set; }
        public int NextPage { get; set; }
        public int PreviousPage { get; set; }
        public List<Entry> NodeEntries { get; set; }

        private readonly byte[] _data;

        public Node(byte[] data)
        {
            _data = data;
            NodeEntries = new List<Entry>();
            Read();
        }

        public void Read()
        {
            SpanReader reader = new SpanReader(_data);

            Flag = reader.ReadInt16();
            EntryCount = reader.ReadInt16();
            NextPage = reader.ReadInt32();
            PreviousPage = reader.ReadInt32();

            if (Flag != 0)
                return;

            for (var i = 0; i < EntryCount / 2; i++)
            {
                int parentID = reader.ReadInt32();
                string name = ReadName(ref reader);
                var entry = Entry.ReadEntryFromBuffer(ref reader);
                entry.Name = name;
                entry.ParentNode = parentID;
                NodeEntries.Add(entry);
                reader.Align(0x04);
            }
        }

        private static string ReadName(ref SpanReader reader)
        {
            var sb = new StringBuilder();

            // TODO: actually use the block's toc for this..
            while (true)
            {
                char c = (char)reader.ReadByte();
                if ((byte)c <= 2)
                    break;
                sb.Append(c);
            }

            reader.Position--;
            reader.Align(0x04);

            return sb.ToString();
        }
    }
}
