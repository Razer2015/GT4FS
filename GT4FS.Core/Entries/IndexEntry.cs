using GT4FS.Core.Packing;

using Syroot.BinaryData.Memory;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GT4FS.Core.Entries;

public class IndexEntry : Entry
{
    public byte[] Indexer { get; set; }
    public PageBase SubPageRef { get; set; }
}
