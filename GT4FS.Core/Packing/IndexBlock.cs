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
    /* Index blocks work in such a way that the game can easily pinpoint the location of the block which
     * contains the entry the game is looking for.
     * Each entry in the index blocks are the "middle" of two blocks. The game will first compare the node ids 
     * through binary searching, *then* the string itself.
     * We only need to store the first difference between the last entry of the previous block, and the first
     * entry of the next block. */
    public class IndexBlock : BlockBase
    {
        public override BlockType Type => BlockType.Index;

        public const int HeaderSize = 0x0C;
        public const int TocEntrySize = 0x08;

        /// <summary>
        /// The previous entry between two entry blocks.
        /// </summary>
        public Entry PrevBlockLastEntry { get; set; }

        /// <summary>
        /// The next entry between two entry blocks.
        /// </summary>
        public Entry NextBlockFirstEntry { get; set; }

        public bool IsMasterBlock { get; set; }

        public IndexBlock(int blockSize)
        {
            BlockSize = blockSize;
            Buffer = new byte[BlockSize];
            _spaceLeft = blockSize - HeaderSize - 8; // Account for the int at the begining of the bottom toc

            LastPosition = HeaderSize;
        }

        public bool HasSpaceToWriteEntry(Entry lastPrevBlockEntry, Entry firstNextBlockEntry)
        {
            string indexName = CompareEntries(lastPrevBlockEntry, firstNextBlockEntry);
            int entrySize = MeasureEntrySize(LastPosition, indexName);

            return entrySize + TocEntrySize <= _spaceLeft;
        }

        public void WriteNextDataEntry(Entry lastPrevBlockEntry, Entry firstNextBlockEntry)
        {
            var blockWriter = new SpanWriter(Buffer);
            blockWriter.Position = LastPosition;

            string indexName = CompareEntries(lastPrevBlockEntry, firstNextBlockEntry);

            int actualSpace = MeasureEntrySize(blockWriter.Position, indexName);
            int entrySize = 4 + Encoding.UTF8.GetByteCount(indexName);

            if (actualSpace > _spaceLeft)
                throw new Exception("Not enough space to write index entry.");

            // Begin to write the entry's common information
            int entryOffset = blockWriter.Position;
            blockWriter.Endian = Endian.Big;
            blockWriter.WriteInt32(firstNextBlockEntry.ParentNode);
            blockWriter.Endian = Endian.Little;
            blockWriter.WriteStringRaw(indexName);
            blockWriter.Align(0x04); // String is aligned

            int endPos = blockWriter.Position;

            _spaceLeft -= actualSpace + TocEntrySize; // Include the block's toc entry

            // Write the lookup information at the end of the block
            blockWriter.Position = BlockSize - ((EntryCount + 1) * TocEntrySize);
            blockWriter.WriteUInt16((ushort)entryOffset);
            blockWriter.WriteUInt16((ushort)entrySize);
            blockWriter.WriteUInt32(0); // We will write the block index later as we don't have it

            // Move on to next.
            EntryCount++;

            LastPosition = endPos;
        }

        /// <summary>
        /// Measures how much space one entry will take (Toc entry excluded).
        /// </summary>
        /// <param name="baseOffset"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        private ushort MeasureEntrySize(int baseOffset, string name)
        {
            int newOffset = baseOffset;
            newOffset += sizeof(int); // ParentNode
            newOffset += Encoding.UTF8.GetByteCount(name); // Name Len

            newOffset += Utils.Align(newOffset, 0x04) - newOffset;
            return (ushort)(newOffset - baseOffset);
        }

        /// <summary>
        /// Compares two entries, to set up index blocks for binary searching.
        /// </summary>
        /// <param name="prevLastBlockEntry"></param>
        /// <param name="nextFirstBlockEntry"></param>
        /// <returns></returns>
        public string CompareEntries(Entry prevLastBlockEntry, Entry nextFirstBlockEntry)
        {
            if (prevLastBlockEntry.ParentNode != nextFirstBlockEntry.ParentNode)
                return string.Empty; // No point returning a file name, the parent node is already enough of a difference

            string lastName = prevLastBlockEntry.Name;
            string firstNextName = nextFirstBlockEntry.Name;

            int maxLen = Math.Max(lastName.Length, firstNextName.Length);
            for (int i = 0; i < maxLen; i++)
            {
                if (i >= lastName.Length)
                    return firstNextName.Substring(0, i + 1);

                if (lastName[i] != firstNextName[i])
                    return firstNextName.Substring(0, i + 1);
            }

            // This is unpossible, or else both entries are the same file due to being the same parent
            throw new ArgumentException($"First entry is equal to the second entry. ({lastName}, parent ID {prevLastBlockEntry.ParentNode})");
        }
    }
}
