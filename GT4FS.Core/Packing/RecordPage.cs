using GT4FS.Core.Entries;

using Syroot.BinaryData;
using Syroot.BinaryData.Core;
using Syroot.BinaryData.Memory;

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GT4FS.Core.Packing;

/// <summary>
/// Record pages contain actual information about files.
/// </summary>
public class RecordPage : PageBase
{
    public override PageType Type => PageType.PT_RECORD;

    public Entry FirstEntry { get; set; }
    public Entry LastEntry { get; set; }

    public RecordPage(ushort pageSize)
        : base(pageSize) { }

    public bool TryAddEntry(RecordEntry entry)
    {
        int entryOffset = PageBase.PAGE_HEADER_SIZE + EntriesInfoSize;
        int newEntryInfoSize = entry.GetTotalSize(entryOffset);
        int currentSpaceTaken = PageBase.PAGE_HEADER_SIZE +
            EntriesInfoSize + // Entries info
            (4 + (Entries.Count * PAGE_TOC_ENTRY_SIZE)); // Bottom toc

        if (currentSpaceTaken + (newEntryInfoSize + PageBase.PAGE_TOC_ENTRY_SIZE) > PageSize)
            return false;

        Entries.Add(entry);
        EntriesInfoSize += newEntryInfoSize;
        return true;
    }

    public override void Serialize(ref SpanWriter writer)
    {
        int basePageOffset = writer.Position;
        writer.WriteUInt16(0); // IsIndexingBlock
        writer.WriteUInt16((ushort)((Entries.Count * 2) + 1));
        writer.WriteInt32(NextPage?.PageIndex ?? -1);
        writer.WriteInt32(PreviousPage?.PageIndex ?? -1);

        int lastEntryOffset = writer.Position;
        for (int i = 0; i < Entries.Count; i++)
        {
            RecordEntry recordEntry = (RecordEntry)Entries[i];
            writer.Position = lastEntryOffset;
            int entryOffset = lastEntryOffset;

            {   // Like IndexPage, this is also an index.
                writer.Endian = Endian.Big;
                writer.WriteInt32(recordEntry.ParentNode);
                writer.Endian = Endian.Little;
                writer.WriteStringRaw(recordEntry.Name);
                writer.Align(0x04);
            }

            int entryDataOffset = writer.Position;
            recordEntry.SerializeEntryInfo(ref writer);
            int dataSize = writer.Position - entryDataOffset;
            writer.Align(0x04);
            lastEntryOffset = writer.Position;

            
            // Reverse order
            writer.Position = (basePageOffset + PageSize) - ((i + 1) * PAGE_TOC_ENTRY_SIZE);
            writer.WriteUInt16((ushort)(entryOffset - basePageOffset));
            writer.WriteUInt16((ushort)recordEntry.GetIndexerSize());
            writer.WriteUInt16((ushort)(entryDataOffset - basePageOffset));
            writer.WriteUInt16((ushort)dataSize);
        }

        // Write end offset terminator - skip to last of page toc and write it behind it
        // NOTE: It doesn't seem to be used, but let's write it anyway
        //       This is similar to the index pages were the very last node ID *would* be stored for bsearch
        //       Except the offset to the "last" entry is written here, even though it's void, so it's effectively the last position
        writer.Position = (basePageOffset + PageSize) - ((Entries.Count) * PAGE_TOC_ENTRY_SIZE) - 4;
        writer.WriteUInt16(0);
        writer.WriteUInt16((ushort)lastEntryOffset);
    }
}
