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
    public const int PAGE_HEADER_SIZE = 0x0C;
    public const int PAGE_TOC_ENTRY_SIZE = 0x08;

    /// <summary>
    /// Type of page.
    /// </summary>
    public abstract PageType Type { get; }

    public int PageIndex { get; set; }
    public PageBase PreviousPage { get; set; }
    public PageBase NextPage { get; set; }

    public int EntriesInfoSize { get; set; }
    public List<Entry> Entries { get; set; } = [];

    public ushort PageSize { get; }

    public PageBase(ushort pageSize)
    {
        PageSize = pageSize;
    }

    public abstract void Serialize(ref SpanWriter writer);

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
