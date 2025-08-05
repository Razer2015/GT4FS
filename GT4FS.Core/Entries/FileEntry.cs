using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using Syroot.BinaryData.Memory;

namespace GT4FS.Core.Entries;

/// <summary>
/// Represents a (non-compressed) file entry in the file system.
/// </summary>
[DebuggerDisplay("FileEntry: {Name}")]
public class FileEntry : RecordEntry
{
    public int Size { get; set; }
    public DateTime ModifiedDate { get; set; } = DateTime.Now;
    public int PageOffset { get; set; }

    public FileEntry() { }
    public FileEntry(string name)
    { 
        Name = name;
        EntryType = RecordType.File;
    }

    public override ushort GetEntryInfoSize()
        => 1 + 4 + 4 + 4; // Type + Page Offset + Date + Size

    public override void SerializeEntryInfo(ref SpanWriter writer)
    {
        writer.WriteByte((byte)EntryType);

        writer.WriteInt32(PageOffset);
        writer.WriteDateTimeT(ModifiedDate);
        writer.WriteInt32(Size);
    }
}
