using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;

using Syroot.BinaryData.Memory;

namespace GT4FS.Core.Packing
{
    [DebuggerDisplay("{Name}")]
    public abstract class Entry
    {
        /// <summary>
        /// Alignment for the entry, for both strings and the entire serialized entry.
        /// </summary>
        public const int Alignment = 0x04;

        public int ParentNode { get; set; }
        public string Name { get; set; }
        public VolumeEntryType EntryType { get; set; }

        public int NodeID { get; set; }

        public abstract ushort GetTypeMetaSize();
        public abstract void SerializeTypeMeta(ref SpanWriter writer);

        public int GetEntryMetaSize()
            => sizeof(int) + Encoding.UTF8.GetByteCount(Name); //  ParentNode + Name Len

        public ushort GetTotalSize(int baseOffset)
        {
            int newOffset = baseOffset;
            newOffset += sizeof(int); // ParentNode
            newOffset += Encoding.UTF8.GetByteCount(Name); // Name Len

            newOffset += Utils.Align(newOffset, Alignment) - newOffset;
            newOffset += GetTypeMetaSize(); // Type Metadata

            // Whole thing is also aligned
            newOffset += Utils.Align(newOffset, Alignment) - newOffset;

            return (ushort)(newOffset - baseOffset);
        }
    }

    public enum VolumeEntryType
    {
        Directory,
        File,
        CompressedFile,
    }
}
