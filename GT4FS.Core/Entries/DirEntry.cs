using System;
using System.Collections.Generic;
using System.Text;

using Syroot.BinaryData.Memory;

namespace GT4FS.Core.Entries
{
    public class DirEntry : Entry
    {
        public SortedDictionary<string, Entry> ChildEntries { get; set; } = new(StringComparer.Ordinal);

        public DirEntry() { }
        public DirEntry(string name)
        { 
            Name = name;
            EntryType = VolumeEntryType.Directory;
        }

        public override ushort GetTypeMetaSize()
            => 1 + 4; // Type + Node ID

        public override void SerializeTypeMeta(ref SpanWriter writer)
        {
            writer.WriteByte((byte)EntryType);
            writer.WriteInt32(NodeID);
        }
    }
}
