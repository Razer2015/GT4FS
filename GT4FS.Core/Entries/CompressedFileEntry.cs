using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using Syroot.BinaryData.Memory;

namespace GT4FS.Core.Entries
{
    [DebuggerDisplay("CompressedFileEntry: {Name}")]
    public class CompressedFileEntry : Entry
    {
        public int CompressedSize { get; set; }
        public int Size { get; set; }
        public DateTime ModifiedDate { get; set; } = DateTime.Now;
        public int PageOffset { get; set; }

        public CompressedFileEntry() { }
        public CompressedFileEntry(string name)
        { 
            Name = name;
            EntryType = VolumeEntryType.CompressedFile;
        }

        public override ushort GetTypeMetaSize()
            => 1 + 4 + 4 + 4 + 4; // Type + Page Offset + Date + Comp Size + Size

        public override void SerializeTypeMeta(ref SpanWriter writer)
        {
            writer.WriteByte((byte)EntryType);

            writer.WriteInt32(PageOffset);
            writer.WriteDateTimeT(ModifiedDate);
            writer.WriteInt32(CompressedSize);
            writer.WriteInt32(Size);
        }
    }
}
