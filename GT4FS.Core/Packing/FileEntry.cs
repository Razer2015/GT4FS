using System;
using System.Collections.Generic;
using System.Text;

using Syroot.BinaryData.Memory;

namespace GT4FS.Core.Packing
{
    public class FileEntry : Entry
    {
        public int Size { get; set; }
        public DateTime ModifiedDate { get; set; }
        public int PageOffset { get; set; }

        public FileEntry(string name)
            => Name = name;

        public override ushort GetMetaSize()
            => 1 + 4 + 4 + 4; // Type + Page Offset + Date + Size

        public override void SerializeTypeMeta(ref SpanWriter writer)
        {
            writer.WriteInt32(PageOffset);
            writer.WriteDateTimeT(ModifiedDate);
            writer.WriteInt32(Size);
        }
    }
}
