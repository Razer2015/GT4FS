using GT.Shared.Polyphony;
using System.Collections.Generic;
using System.IO;

using Syroot.BinaryData;

namespace GT4FS.Core {
    public class Node {
        public short Flag { get; set; }
        public short EntryCount { get; set; }
        public int NextPage { get; set; }
        public int PreviousPage { get; set; }
        public List<NodeEntry> NodeEntries { get; set; }

        private readonly byte[] _data;

        public Node(byte[] data) {
            _data = data;
            NodeEntries = new List<NodeEntry>();
            Read();
        }

        public void Read()
        {
            using (var ms = new MemoryStream(_data))
            using (var reader = new BinaryStream(ms))
            {
                Flag = reader.ReadInt16();
                EntryCount = reader.ReadInt16();
                NextPage = reader.ReadInt32();
                PreviousPage = reader.ReadInt32();

                if (Flag != 0) 
                    return;

                for (var i = 0; i < EntryCount / 2; i++)
                    NodeEntries.Add(new NodeEntry(reader));
            }
        }
    }
}
