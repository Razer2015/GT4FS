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

namespace GT4FS.Core.Packing;

/// <summary>
/// Represents a table of content page. This class is abstract.
/// </summary>
public abstract class PageBase
{
    /// <summary>
    /// Type of page.
    /// </summary>
    public abstract PageType Type { get; }

    public int LastPosition { get; set; }

    /// <summary>
    /// Buffer for the page.
    /// </summary>
    public byte[] Buffer { get; set; }

    /// <summary>
    /// Page size.
    /// </summary>
    public int PageSize { get; set; }

    public int PageIndex { get; set; }

    public PageBase PreviousPage { get; set; }
    public PageBase NextPage { get; set; }

    public int EntryCount { get; set; }
    protected int _spaceLeft;

    public abstract void FinalizeHeader();

    public void WriteNextPage(int next)
        => BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan()[4..], next);

    public void WritePreviousPage(int previous)
        => BinaryPrimitives.WriteInt32LittleEndian(Buffer.AsSpan()[8..], previous);


    public enum PageType : ushort
    {
        /// <summary>
        /// Stores information about the file entries.
        /// </summary>
        PT_RECORD,

        /// <summary>
        /// Stores page indexing information, to then point to entry pages.
        /// </summary>
        PT_INDEX,
    }
}
