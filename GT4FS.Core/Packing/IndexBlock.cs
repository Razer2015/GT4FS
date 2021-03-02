using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Buffers.Binary;

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

        /// <summary>
        /// Compares two entries and determines whether they can be writen into the block.
        /// </summary>
        /// <param name="lastPrevBlockEntry"></param>
        /// <param name="firstNextBlockEntry"></param>
        /// <returns></returns>
        public bool HasSpaceToWriteEntry(Entry lastPrevBlockEntry, Entry firstNextBlockEntry)
        {
            byte[] indexer = CompareEntries(lastPrevBlockEntry, firstNextBlockEntry);
            int entrySize = MeasureEntrySize(LastPosition, indexer);

            return entrySize + TocEntrySize <= _spaceLeft;
        }

        public void WriteNextDataEntry(Entry lastPrevBlockEntry, Entry firstNextBlockEntry)
        {
            var blockWriter = new SpanWriter(Buffer);
            blockWriter.Position = LastPosition;

            byte[] indexer = CompareEntries(lastPrevBlockEntry, firstNextBlockEntry);

            int actualSpace = MeasureEntrySize(blockWriter.Position, indexer);
            int entrySize = indexer.Length;

            if (actualSpace > _spaceLeft)
                throw new Exception("Not enough space to write index entry.");

            // Begin to write the entry's common information
            int entryOffset = blockWriter.Position;
            blockWriter.WriteBytes(indexer);
            blockWriter.Align(0x04); // Entry is aligned

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
        /// Fills up the block's type, and entry count.
        /// </summary>
        public override void FinalizeHeader()
        {
            var blockWriter = new SpanWriter(Buffer);
            blockWriter.Position = LastPosition;

            // Write up the block info - write what we can write - the entry count
            blockWriter.Position = 0;
            blockWriter.WriteUInt16((ushort)Type);
            blockWriter.WriteUInt16((ushort)((EntryCount * 2) + 1));
        }

        /// <summary>
        /// Measures how much space one entry will take (Toc entry excluded).
        /// </summary>
        /// <param name="baseOffset"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        private ushort MeasureEntrySize(int baseOffset, byte[] indexer)
        {
            int newOffset = baseOffset;
            newOffset += indexer.Length;

            newOffset += Utils.Align(newOffset, 0x04) - newOffset;
            return (ushort)(newOffset - baseOffset);
        }

        /// <summary>
        /// Compares two entries, to set up index blocks for binary searching.
        /// </summary>
        /// <param name="prevLastBlockEntry"></param>
        /// <param name="nextFirstBlockEntry"></param>
        /// <returns></returns>
        public byte[] CompareEntries(Entry prevLastBlockEntry, Entry nextFirstBlockEntry)
        {
            // The entry is the combination of the parent node, and the entry name.
            // We are writing the first difference, including in the parent node's int.
            // An entry may aswell be as low as 1 byte if the parent node takes up the whole int's space, and it is just 1 different.
            // The game will loop through the indexer's buffer, disregarding what they are. Just checking if its different.

            if (prevLastBlockEntry.ParentNode != nextFirstBlockEntry.ParentNode)
            {
                // The entry will only have the parent node difference - its a different folder

                Span<byte> prevNodeID = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(prevNodeID, prevLastBlockEntry.ParentNode);

                Span<byte> nextNodeID = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(nextNodeID, nextFirstBlockEntry.ParentNode);

                for (int i = 0; i < 4; i++)
                {
                    if (prevNodeID[i] != nextNodeID[i])
                        return nextNodeID.Slice(0, i + 1).ToArray();
                }
            }

            // Same folder, different file name by this point

            string lastName = prevLastBlockEntry.Name;
            string firstNextName = nextFirstBlockEntry.Name;

            int maxLen = Math.Max(lastName.Length, firstNextName.Length);
            for (int i = 0; i < maxLen; i++)
            {
                if (i >= lastName.Length || lastName[i] != firstNextName[i])
                {
                    // Append node id and name to for our final entry
                    byte[] difference = new byte[4 + (i + 1)];
                    BinaryPrimitives.WriteInt32BigEndian(difference, nextFirstBlockEntry.ParentNode);
                    Encoding.UTF8.GetBytes(firstNextName.AsSpan(0, i + 1), difference.AsSpan(4));
                    return difference;
                }
            }

            // This is unpossible, or else both entries are the same file due to being the same parent
            throw new ArgumentException($"First entry is equal to the second entry. ({lastName}, parent ID {prevLastBlockEntry.ParentNode})");
        }
    }
}
