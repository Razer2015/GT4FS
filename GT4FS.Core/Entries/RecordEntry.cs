using GT4FS.Core.Packing;

using Syroot.BinaryData.Memory;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GT4FS.Core.Entries;

/// <summary>
/// Represents an entry that is either a file or a folder on the file system.
/// </summary>
public abstract class RecordEntry : Entry
{
    /// <summary>
    /// For appending mode, not actually part of the entry's data.
    /// </summary>
    public bool IsModFileAppendToVolumeEnd { get; set; }

    /// <summary>
    /// For appending mode, not actually part of the entry's data.
    /// </summary>
    public string VolumePath { get; set; }

    /// <summary>
    /// For appending mode, not actually part of the entry's data.
    /// </summary>
    public string AbsolutePath { get; set; }

    public RecordType EntryType { get; set; }

    public int NodeID { get; set; }

    public abstract ushort GetEntryInfoSize();
    public abstract void SerializeEntryInfo(ref SpanWriter writer);

    public ushort GetTotalSize(int baseOffset)
    {
        int newOffset = baseOffset;
        newOffset += sizeof(int); // ParentNode
        newOffset += Encoding.UTF8.GetByteCount(Name); // Name Len

        newOffset += Utils.Align(newOffset, Alignment) - newOffset;
        newOffset += GetEntryInfoSize(); // Type Metadata

        // Whole thing is also aligned
        newOffset += Utils.Align(newOffset, Alignment) - newOffset;

        return (ushort)(newOffset - baseOffset);
    }

    public static RecordEntry ReadEntryInfo(ref SpanReader sr)
    {
        RecordEntry entry = null;
        RecordType type = (RecordType)sr.ReadByte();
        if (type == RecordType.CompressedFile)
        {
            entry = new CompressedFileEntry();
            ((CompressedFileEntry)entry).EntryType = type;
            ((CompressedFileEntry)entry).PageOffset = sr.ReadInt32();
            ((CompressedFileEntry)entry).ModifiedDate = sr.ReadDateTimeT();
            ((CompressedFileEntry)entry).CompressedSize = sr.ReadInt32();
            ((CompressedFileEntry)entry).Size = sr.ReadInt32();
        }
        else if (type == RecordType.File)
        {
            entry = new FileEntry();
            ((FileEntry)entry).EntryType = type;
            ((FileEntry)entry).PageOffset = sr.ReadInt32();
            ((FileEntry)entry).ModifiedDate = sr.ReadDateTimeT();
            ((FileEntry)entry).Size = sr.ReadInt32();
            ((FileEntry)entry).EntryType = type;
        }
        else if (type == RecordType.Directory)
        {
            entry = new DirEntry();
            ((DirEntry)entry).EntryType = type;
            ((DirEntry)entry).NodeID = sr.ReadInt32();
        }

        return entry;
    }
}

public enum RecordType
{
    Directory,
    File,
    CompressedFile,
}

