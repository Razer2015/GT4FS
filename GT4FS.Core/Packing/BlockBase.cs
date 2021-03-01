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

        public abstract void FinalizeHeader();

        public void WriteNextPage(int next)
            => BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan()[4..], next);

        public void WritePreviousPage(int previous)
            => BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan()[8..], previous);


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
