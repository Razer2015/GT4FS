using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Syroot.BinaryData.Core;
using Syroot.BinaryData;
using Syroot.BinaryData.Memory;

using GT4FS.Core.Entries;

namespace GT4FS.Core.Packing
{
    public class EntryDescriptorPage : PageBase
    {
        public override PageType Type => PageType.Entry;

        public const int HeaderSize = 0x0C;
        public const int TocEntrySize = 0x08;

        public Entry FirstEntry { get; set; }
        public Entry LastEntry { get; set; }

        public EntryDescriptorPage(int pageSize)
        {
            PageSize = pageSize;
            Buffer = new byte[PageSize];
            _spaceLeft = pageSize - HeaderSize - TocEntrySize; // Account for the last page entry "terminator" behind the bottom ToC

            LastPosition = HeaderSize;
        }

        public bool HasSpaceToWriteEntry(Entry entry)
        {
            int entrySize = entry.GetTotalSize(LastPosition);
            return entrySize + TocEntrySize <= _spaceLeft;   
        }

        public void WriteEntry(Entry entry)
        {
            int entrySize = entry.GetTotalSize(LastPosition);
            if (entrySize + TocEntrySize > _spaceLeft)
                throw new Exception("Not enough space to write entry.");

            if (EntryCount == 0)
                FirstEntry = entry;

            SpanWriter pageWriter = new SpanWriter(Buffer);
            pageWriter.Position = LastPosition;

            // Begin to write the entry's common information
            // Not actually BE, both are writen as indexing buffer
            int entryOffset = pageWriter.Position;
            pageWriter.Endian = Endian.Big;
            pageWriter.WriteInt32(entry.ParentNode);
            pageWriter.Endian = Endian.Little;
            pageWriter.WriteStringRaw(entry.Name);
            pageWriter.Align(0x04); // Entry is aligned

            // Write type specific
            int entryMetaOffset = pageWriter.Position;
            entry.SerializeTypeMeta(ref pageWriter);
            pageWriter.Align(0x04); // Whole entry is also aligned

            LastPosition = pageWriter.Position;
            _spaceLeft -= entrySize + TocEntrySize; // Include the page's toc entry

            // Write the lookup information at the end of the page
            pageWriter.WriteUInt16((ushort)entryOffset);
            pageWriter.WriteUInt16((ushort)entry.GetEntryMetaSize());
            pageWriter.WriteUInt16((ushort)entryMetaOffset);
            pageWriter.WriteUInt16(entry.GetTypeMetaSize());

            // Move on to next.
            EntryCount++;
        }

        public override void FinalizeHeader()
        {
            var pageWriter = new SpanWriter(Buffer);
            pageWriter.Position = LastPosition;

            // Write up the page info - write what we can write - the entry count
            pageWriter.Position = 0;
            pageWriter.WriteUInt16((ushort)Type);
            pageWriter.WriteUInt16((ushort)((EntryCount * 2) + 1));

            // Write end offset terminator - skip to last of page toc and write it behind it
            // NOTE: It doesn't seem to be used, but let's write it anyway
            //       This is similar to the index pages were the very last node ID is stored for bsearch
            //       Except the offset to the "last" entry is stored here, even though it's void, so it's effectively the last position
            pageWriter.Position = PageSize - (EntryCount * TocEntrySize) - TocEntrySize;
            pageWriter.WriteInt16(0);
            pageWriter.WriteInt16(0);
            pageWriter.WriteInt16(0);
            pageWriter.WriteInt16((short)LastPosition);

        }
    }
}
