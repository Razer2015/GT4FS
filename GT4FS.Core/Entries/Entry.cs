using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;

using Syroot.BinaryData.Memory;
using GT4FS.Core.Packing;

namespace GT4FS.Core.Entries;

[DebuggerDisplay("Entry: {Name}")]
public abstract class Entry
{
    /// <summary>
    /// Alignment for the entry, for both strings and the entire serialized entry.
    /// </summary>
    public const int Alignment = 0x04;

    public int ParentNode { get; set; }
    public string Name { get; set; }

    public int GetIndexerSize()
        => sizeof(int) + Encoding.UTF8.GetByteCount(Name); //  ParentNode + Name Len
}