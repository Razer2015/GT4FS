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
using System.Diagnostics;

namespace GT4FS.Core.Packing;

/* Index pages work in such a way that the game can easily pinpoint the location of the page which
 * contains the entry the game is looking for.
 * Each entry in the index pages are the "middle" of two pages. The game will first compare the node ids 
 * through binary searching, *then* the string itself.
 * We only need to store the first difference between the last entry of the previous page, and the first
 * entry of the next page. */
public class IndexPage : PageBase
{
    public override PageType Type => PageType.PT_INDEX;

    /// <summary>
    /// The previous entry between two entry pages.
    /// </summary>
    public Entry PrevPageLastEntry { get; set; }

    /// <summary>
    /// The next entry between two entry pages.
    /// </summary>
    public Entry NextPageFirstEntry { get; set; }

    public PageBase LastPage { get; set; }

    public IndexPage(ushort pageSize)
        : base(pageSize) { }

    public bool TryAddEntry(Entry lastPrevPageEntry, Entry firstNextPageEntry, PageBase targetPage)
    {
        (byte[] indexer, int parentNode, string name) = CompareEntries(lastPrevPageEntry, firstNextPageEntry);
        int entryOffset = PAGE_HEADER_SIZE + EntriesInfoSize;
        int newEntryInfoSize = MeasureEntrySize(entryOffset, indexer);
        int currentSpaceTaken = PAGE_HEADER_SIZE +
            EntriesInfoSize + // Entries info
            (4 + (Entries.Count * PAGE_TOC_ENTRY_SIZE)); // Bottom toc

        if (currentSpaceTaken + (newEntryInfoSize + PageBase.PAGE_TOC_ENTRY_SIZE) > PageSize)
            return false;

        Entries.Add(new IndexEntry() 
        { 
            Indexer = indexer,
            ParentNode = parentNode,
            Name = name,
            SubPageRef = targetPage 
        });

        EntriesInfoSize += newEntryInfoSize;
        return true;
    }

    public override void Serialize(ref SpanWriter writer)
    {
        long basePageOffset = writer.Position;
        writer.WriteUInt16(1); // IsIndexingBlock
        writer.WriteUInt16((ushort)((Entries.Count * 2) + 1));
        writer.WriteInt32(NextPage?.PageIndex ?? -1);
        writer.WriteInt32(PreviousPage?.PageIndex ?? -1);

        int lastEntryOffset = writer.Position;
        for (int i = 0; i < Entries.Count; i++)
        {
            IndexEntry indexEntry = (IndexEntry)Entries[i];

            writer.Position = lastEntryOffset;
            long entryOffset = lastEntryOffset;
            writer.WriteBytes(indexEntry.Indexer);
            writer.Align(0x04);
            lastEntryOffset = writer.Position;

            // Reverse order
            writer.Position = (int)((basePageOffset + PageSize) - ((i + 1) * PAGE_TOC_ENTRY_SIZE));
            writer.WriteUInt16((ushort)(entryOffset - basePageOffset));
            writer.WriteUInt16((ushort)indexEntry.Indexer.Length);
            writer.WriteInt32(indexEntry.SubPageRef.PageIndex);
        }

        writer.Position = (int)((basePageOffset + PageSize) - (Entries.Count * PAGE_TOC_ENTRY_SIZE)) - 4;
        writer.WriteInt32(LastPage.PageIndex); // IndexEnd
    }

    /// <summary>
    /// Measures how much space one entry will take (Toc entry excluded).
    /// </summary>
    /// <param name="baseOffset"></param>
    /// <param name="name"></param>
    /// <returns></returns>
    private static ushort MeasureEntrySize(int baseOffset, byte[] indexer)
    {
        int newOffset = baseOffset;
        newOffset += indexer.Length;
        newOffset += Utils.Align(newOffset, 0x04) - newOffset;
        return (ushort)(newOffset - baseOffset);
    }

    /// <summary>
    /// Compares two entries, to set up index pages for binary searching.
    /// </summary>
    /// <param name="prevLastPageEntry"></param>
    /// <param name="nextFirstPageEntry"></param>
    /// <returns></returns>
    public (byte[] Indexer, int ParentNode, string Name) CompareEntries(Entry prevLastPageEntry, Entry nextFirstPageEntry)
    {
        // The entry is the combination of the parent node, and the entry name.
        // We are writing the first difference, including in the parent node's int.
        // An entry may aswell be as low as 1 byte if the parent node takes up the whole int's space, and it is just 1 different.
        // The game will loop through the indexer's buffer, disregarding what they are. Just checking if its different.

        if (prevLastPageEntry.ParentNode != nextFirstPageEntry.ParentNode)
        {
            // The entry will only have the parent node difference - its a different folder

            Span<byte> prevNodeID = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(prevNodeID, prevLastPageEntry.ParentNode);

            Span<byte> nextNodeID = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(nextNodeID, nextFirstPageEntry.ParentNode);

            for (int i = 0; i < 4; i++)
            {
                if (prevNodeID[i] != nextNodeID[i])
                    return (nextNodeID.Slice(0, i + 1).ToArray(), nextFirstPageEntry.ParentNode, nextFirstPageEntry.Name);
            }
        }

        // Same folder, different file name by this point

        string lastName = prevLastPageEntry.Name;
        string firstNextName = nextFirstPageEntry.Name;

        int maxLen = Math.Max(lastName.Length, firstNextName.Length);
        for (int i = 0; i < maxLen; i++)
        {
            if (i >= lastName.Length || lastName[i] != firstNextName[i])
            {
                // Append node id and name to for our final entry
                byte[] difference = new byte[4 + (i + 1)];
                BinaryPrimitives.WriteInt32BigEndian(difference, nextFirstPageEntry.ParentNode);
                Encoding.UTF8.GetBytes(firstNextName.AsSpan(0, i + 1), difference.AsSpan(4));
                return (difference, nextFirstPageEntry.ParentNode, firstNextName.Substring(0, i + 1));
            }
        }

        // This is unpossible, or else both entries are the same file due to being the same parent
        throw new UnreachableException($"First entry is equal to the second entry. ({lastName}, parent ID {prevLastPageEntry.ParentNode})");
    }
}
