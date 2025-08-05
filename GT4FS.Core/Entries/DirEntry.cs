using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using Syroot.BinaryData.Memory;

namespace GT4FS.Core.Entries;

/// <summary>
/// Represents a directory entry in the file system.
/// </summary>
[DebuggerDisplay("DirEntry: {Name}")]
public class DirEntry : RecordEntry
{
    public SortedDictionary<string, RecordEntry> ChildEntries { get; set; } = new(StringComparer.Ordinal);

    public DirEntry() { }
    public DirEntry(string name)
    { 
        Name = name;
        EntryType = RecordType.Directory;
    }

    public override ushort GetEntryInfoSize()
        => 1 + 4; // Type + Node ID

    public override void SerializeEntryInfo(ref SpanWriter writer)
    {
        writer.WriteByte((byte)EntryType);
        writer.WriteInt32(NodeID);
    }
}
