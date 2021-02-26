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
    public abstract class BlockBase
    {
        public abstract BlockType Type { get; }

        public int LastPosition { get; set; }
        public byte[] Buffer { get; set; }
        public int BlockSize { get; set; }

        public int BlockIndex { get; set; }

        public BlockBase PreviousBlock { get; set; }
        public BlockBase NextBlock { get; set; }

        public int EntryCount { get; set; }
        protected int _spaceLeft;

        public void FinalizeHeader()
        {
            var blockWriter = new SpanWriter(Buffer);
            blockWriter.Position = LastPosition;

            // Write up the block info - write what we can write - the entry count
            blockWriter.Position = 0;
            blockWriter.WriteUInt16((ushort)Type);
            blockWriter.WriteUInt16((ushort)((EntryCount * 2) + 1));
        }

        public void WriteNextPage(int next)
        {
            SpanWriter blockWriter = new SpanWriter(Buffer);
            blockWriter.Position = 0x04;

            blockWriter.WriteInt32(next);
        }

        public void WritePreviousPage(int previous)
        {
            SpanWriter blockWriter = new SpanWriter(Buffer);
            blockWriter.Position = 0x08;

            blockWriter.WriteInt32(previous);
        }

        public enum BlockType : ushort
        {
            /// <summary>
            /// Stores information about the file entries.
            /// </summary>
            Entry,

            /// <summary>
            /// Stores block indexing information, to then point to entry blocks.
            /// </summary>
            Index,
        }
    }
}
