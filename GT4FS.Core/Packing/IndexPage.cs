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

/* Index pages work in such a way that the game can easily pinpoint the location of the page which
 * contains the entry the game is looking for.
 * Each entry in the index pages are the "middle" of two pages. The game will first compare the node ids 
 * through binary searching, *then* the string itself.
 * We only need to store the first difference between the last entry of the previous page, and the first
 * entry of the next page. */
public class IndexPage : PageBase
{
    public override PageType Type => PageType.PT_INDEX;

    public const int HeaderSize = 0x0C;
    public const int TocEntrySize = 0x08;

    /// <summary>
    /// The previous entry between two entry pages.
    /// </summary>
    public Entry PrevPageLastEntry { get; set; }

    /// <summary>
    /// The next entry between two entry pages.
    /// </summary>
    public Entry NextPageFirstEntry { get; set; }

    public bool IsMasterPage { get; set; }

    public IndexPage(int pageSize)
    {
        PageSize = pageSize;
        Buffer = new byte[PageSize];
        _spaceLeft = pageSize - HeaderSize - TocEntrySize; // Account for the last page entry "terminator" behind the bottom ToC

        LastPosition = HeaderSize;
    }

    /// <summary>
    /// Compares two entries and determines whether they can be writen into the page.
    /// </summary>
    /// <param name="lastPrevPageEntry"></param>
    /// <param name="firstNextPageEntry"></param>
    /// <returns></returns>
    public bool HasSpaceToWriteEntry(Entry lastPrevPageEntry, Entry firstNextPageEntry)
    {
        byte[] indexer = CompareEntries(lastPrevPageEntry, firstNextPageEntry);
        int entrySize = MeasureEntrySize(LastPosition, indexer);

        return entrySize + TocEntrySize <= _spaceLeft;
    }

    public void WriteNextDataEntry(Entry lastPrevPageEntry, Entry firstNextPageEntry)
    {
        var pageWriter = new SpanWriter(Buffer);
        pageWriter.Position = LastPosition;

        byte[] indexer = CompareEntries(lastPrevPageEntry, firstNextPageEntry);

        int actualSpace = MeasureEntrySize(pageWriter.Position, indexer);
        int entrySize = indexer.Length;

        if (actualSpace > _spaceLeft)
            throw new Exception("Not enough space to write index entry.");

        // Begin to write the entry's common information
        int entryOffset = pageWriter.Position;
        pageWriter.WriteBytes(indexer);
        pageWriter.Align(0x04); // Entry is aligned

        int endPos = pageWriter.Position;

        _spaceLeft -= actualSpace + TocEntrySize; // Include the page's toc entry

        // Write the lookup information at the end of the page
        pageWriter.Position = PageSize - ((EntryCount + 1) * TocEntrySize);
        pageWriter.WriteUInt16((ushort)entryOffset);
        pageWriter.WriteUInt16((ushort)entrySize);
        pageWriter.WriteUInt32(0); // We will write the page index later as we don't have it

        // Move on to next.
        EntryCount++;

        LastPosition = endPos;
    }

    /// <summary>
    /// Fills up the page's type, and entry count.
    /// </summary>
    public override void FinalizeHeader()
    {
        var pageWriter = new SpanWriter(Buffer);
        pageWriter.Position = LastPosition;

        // Write up the page info - write what we can write - the entry count
        pageWriter.Position = 0;
        pageWriter.WriteUInt16((ushort)Type);
        pageWriter.WriteUInt16((ushort)((EntryCount * 2) + 1));

        // Entry terminator will be written later on
    }

    /// <summary>
    /// Measures how much space one entry will take (Toc entry excluded).
    /// </summary>
    /// <param name="baseOffset"></param>
    /// <param name="name"></param>
    /// <returns></returns>
    private ushort MeasureEntrySize(int baseOffset, byte[] indexer)
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
    public byte[] CompareEntries(Entry prevLastPageEntry, Entry nextFirstPageEntry)
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
                    return nextNodeID.Slice(0, i + 1).ToArray();
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
                return difference;
            }
        }

        // This is unpossible, or else both entries are the same file due to being the same parent
        throw new ArgumentException($"First entry is equal to the second entry. ({lastName}, parent ID {prevLastPageEntry.ParentNode})");
    }
}
