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
    public class EntryBlock : BlockBase
    {
        public override BlockType Type => BlockType.Entry;

        public const int HeaderSize = 0x0C;
        public const int TocEntrySize = 0x08;

        public Entry FirstEntry { get; set; }
        public Entry LastEntry { get; set; }

        public EntryBlock(int blockSize)
        {
            BlockSize = blockSize;
            Buffer = new byte[BlockSize];
            _spaceLeft = blockSize - HeaderSize - 8; // Account for the block terminator

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

            SpanWriter blockWriter = new SpanWriter(Buffer);
            blockWriter.Position = LastPosition;

            // Begin to write the entry's common information
            // Not actually BE, both are writen as indexing buffer
            int entryOffset = blockWriter.Position;
            blockWriter.Endian = Endian.Big;
            blockWriter.WriteInt32(entry.ParentNode);
            blockWriter.Endian = Endian.Little;
            blockWriter.WriteStringRaw(entry.Name);
            blockWriter.Align(0x04); // Entry is aligned

            // Write type specific
            int entryMetaOffset = blockWriter.Position;
            entry.SerializeTypeMeta(ref blockWriter);
            blockWriter.Align(0x04); // Whole entry is also aligned

            LastPosition = blockWriter.Position;
            _spaceLeft -= entrySize + TocEntrySize; // Include the block's toc entry

            // Write the lookup information at the end of the block
            blockWriter.Position = BlockSize - ((EntryCount + 1) * TocEntrySize);
            blockWriter.WriteUInt16((ushort)entryOffset);
            blockWriter.WriteUInt16((ushort)entry.GetEntryMetaSize());
            blockWriter.WriteUInt16((ushort)entryMetaOffset);
            blockWriter.WriteUInt16(entry.GetTypeMetaSize());

            // Move on to next.
            EntryCount++;
        }

        public override void FinalizeHeader()
        {
            var blockWriter = new SpanWriter(Buffer);
            blockWriter.Position = LastPosition;

            // Write up the block info - write what we can write - the entry count
            blockWriter.Position = 0;
            blockWriter.WriteUInt16((ushort)Type);
            blockWriter.WriteUInt16((ushort)((EntryCount * 2) + 1));

            // Write end offset terminator - skip to last of block toc and write it behind it
            blockWriter.Position = BlockSize - (EntryCount * TocEntrySize);
            blockWriter.Position -= 2;
            blockWriter.WriteInt16((short)LastPosition);

        }
    }
}
