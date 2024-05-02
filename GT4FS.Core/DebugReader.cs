using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;

using Syroot.BinaryData;
using Syroot.BinaryData.Memory;

using GT4FS.Core.Packing;
using GT4FS.Core.Entries;

namespace GT4FS.Core;

/// <summary>
/// Reader based on reversed game code for debugging purposes.
/// </summary>
public class DebugReader
{
    public int TocOffset { get; set; }

    public BinaryStream VolumeStream { get; set; }
    public ushort PageSize { get; set; } = 0x800;
    public ushort GetPageSize()
        => PageSize;

    // Non original, just to init it
    public static DebugReader FromVolume(string volume, int tocOffset)
    {
        var debugReader = new DebugReader();
        debugReader.TocOffset = tocOffset;
        var fs = new FileStream(volume, FileMode.Open);
        debugReader.VolumeStream = new BinaryStream(fs);
        return debugReader;
    }

    public Entry TraversePathFindEntry(int parentID, string path)
    {
        Entry entry = null;

        Debug.WriteLine($"Begin path traversal: '{path}'");
        foreach (var part in path.Split('/'))
        {
            Debug.WriteLine($"Traversing: {part}, with parent node ID {parentID}");
            entry = GetEntryOfPathPart(parentID, part, part.Length);

            if (entry.EntryType == VolumeEntryType.File || entry.EntryType == VolumeEntryType.CompressedFile)
                return entry;

            parentID = entry.NodeID;
        }

        return entry;
    }

    public Entry GetEntryOfPathPart(int parentID, string part, int partLength)
    {
        // Create input entry
        byte[] entryInput = new byte[4 + partLength];
        BinaryPrimitives.WriteInt32BigEndian(entryInput, parentID);
        Encoding.ASCII.GetBytes(part, entryInput.AsSpan(4));

        PageInfo page = GetPage(0);
        while (page.IsIndexPage)
        {
            Debug.WriteLine($"Searching index page ({page.PageIndex}) for {part}");
            int nextPageIndex = page.SearchIndexPage(entryInput, entryInput.Length);
            page.SwitchToNewIndex(nextPageIndex);

            if (page.IsIndexPage)
                Debug.WriteLine($"Next index page to search is: {page.PageIndex}");
        }

        Debug.WriteLine($"{part} ({parentID}) is located at page index: {page.PageIndex}");

        // At that point we have the page of which the entry we're looking for is in
        if (!page.SearchEntry(entryInput, entryInput.Length, out int indexResult))
            return null; // return default entry
        else
        {
            Debug.WriteLine($"Found {part} at page {page.PageIndex} with entry index {indexResult}");
            // Read entry and return it using the index
            SpanReader sr = new SpanReader(page.PageBuffer);
            sr.Position = PageSize - (indexResult * 0x08);
            sr.Position -= 0x04; // Skip to the actual entry's type metadata

            short entryTypeMetaOffset = sr.ReadInt16();
            sr.Position = entryTypeMetaOffset;
            var entry = Entry.ReadEntryFromBuffer(ref sr);
            entry.ParentNode = parentID;
            return entry;
        }
    }

    public PageInfo GetPage(int index)
    {
        PageInfo page = new PageInfo();
        page.ParentVolume = this;
        page.PageIndex = -1;
        page.PageBuffer = GetPageBuffer(index);

        Debug.Assert(page.PageBuffer.Length == page.ParentVolume.PageSize);

        if (page.PageBuffer != null)
            page.PageIndex = index;

        return page;
    }

    public byte[] GetPageBuffer(int index)
    {
        if (!IsEncryptedRoFSMagic())
        {
            int beginOffset = GetEntryOffset(index);
            int endOffset = GetEntryOffset(index + 1);

            using var decompressStream = new MemoryStream();
            using var decompressionStream = new DeflateStream(new MemoryStream(VolumeStream.ReadBytes(endOffset - beginOffset)), CompressionMode.Decompress);
            decompressionStream.CopyTo(decompressStream);
            return decompressStream.ToArray();
        }
        else
        {
            int beginOffset = GetEntryOffsetSecure(index);
            int endOffset = GetEntryOffsetSecure(index + 1);
            return DecryptPage(VolumeStream, TocOffset + beginOffset, endOffset - beginOffset);
        }
    }

    public int GetEntryOffset(int pageIndex)
    {
        VolumeStream.Position = TocOffset + TocHeader.HeaderSize + (pageIndex * 4);
        return VolumeStream.ReadInt32();
    }

    public int GetEntryOffsetSecure(int pageIndex)
    {
        VolumeStream.Position = TocOffset + TocHeader.HeaderSize + (pageIndex * 4);
        return VolumeStream.ReadInt32() ^ pageIndex * Volume.OffsetCryptKey + Volume.OffsetCryptKey;
    }

    public bool IsEncryptedRoFSMagic()
    {
        VolumeStream.Position = TocOffset;
        return VolumeStream.ReadInt32(ByteConverter.Big) == TocHeader.MagicValueEncrypted;
    }

    public static int CompareEntries(byte[] entry1, int entry1Len, byte[] entry2, int entry2Len)
    {
#if DEBUG
        int pNode1 = BinaryPrimitives.ReadInt32BigEndian(entry1.AsSpan(0, 4));
        int pNode2 = BinaryPrimitives.ReadInt32BigEndian(entry2.AsSpan(0, 4));

        string str1 = entry1Len > 4 ? Encoding.ASCII.GetString(entry1.AsSpan(4)) : "<empty>";
        string str2 = entry2Len > 4 ? Encoding.ASCII.GetString(entry2.AsSpan(4)) : "<empty>";

        Debug.WriteLine($"Comparing: {str1} ({pNode1}) <-> {str2} ({pNode2})");
#endif

        if (entry1Len > entry2Len)
        {
            if (entry2Len == 0)
                return 1;

            int parentNode1 = BinaryPrimitives.ReadInt32BigEndian(entry1.AsSpan(0, 4));
            int parentNode2 = BinaryPrimitives.ReadInt32BigEndian(entry2.AsSpan(0, 4));

            int nodeDiff = parentNode1 - parentNode2;
            if (nodeDiff != 0)
                return nodeDiff;

            for (int i = 0; i < entry2Len; i++)
            {
                if (entry1[i] - entry2[i] != 0)
                    return entry1[i] - entry2[i];
            }

            return 1;
        }
        else
        {
            if (entry1Len == 0)
                return -1;

            int parentNode1 = BinaryPrimitives.ReadInt32BigEndian(entry1.AsSpan(0, 4));
            int parentNode2 = BinaryPrimitives.ReadInt32BigEndian(entry2.AsSpan(0, 4));

            int nodeDiff = parentNode1 - parentNode2;
            if (nodeDiff != 0)
                return nodeDiff;

            for (int i = 0; i < entry1Len; i++)
            {
                if (entry1[i] - entry2[i] != 0)
                    return entry1[i] - entry2[i];
            }

        }

        return 0;
    }

    // Non original, borrowed
    public byte[] DecryptPage(BinaryStream bs, long offset, int length)
    {
        bs.ByteConverter = ByteConverter.Little;
        bs.BaseStream.Seek(offset, SeekOrigin.Begin);
        using (var decompressStream = new MemoryStream())
        {
            using (var decompressionStream = new DeflateStream(new MemoryStream(bs.ReadBytes(length)), CompressionMode.Decompress))
            {
                decompressionStream.CopyTo(decompressStream);
                return PS2Zip.XorEncript(decompressStream.ToArray(), Volume.DataCryptKey);
            }
        }
    }
    

    // Non original
    public void DebugWriteEntryInfos()
    {
        VolumeStream.Position = TocOffset + 0x12;
        ushort pageCount = VolumeStream.ReadUInt16();

        using var sw = new StreamWriter("page_info.txt");

        for (int i = 0; i < pageCount; i++)
        {
            PageInfo page = GetPage(i);
            SpanReader sr = new SpanReader(page.PageBuffer);

            short pageType = sr.ReadInt16();
            short entryCount = sr.ReadInt16();
            int realEntryCount = (entryCount / 2);

            sw.WriteLine($"Page #{i} {(pageType == 1 ? "[INDEXER]" : "")} - {entryCount} entries [{realEntryCount} actual]");
            sr.Position = PageSize - (realEntryCount * 0x08);

            if (pageType == 1)
            {
                sr.Position -= 4;
                sw.WriteLine($"Last Entry Index: {sr.ReadInt32()}");
            }
            else
            {
                sr.Position -= 2;
                sw.WriteLine($"Offset: {sr.ReadInt16()}");
            }

            for (int j = 0; j < realEntryCount; j++)
            {
                sr.Position = PageSize - (j * 0x08) - 0x08;
                short entryOffset = sr.ReadInt16();
                short entryLen = sr.ReadInt16();

                if (pageType == 1)
                {
                    int pageIndex = sr.ReadInt16();
                    sr.Position = entryOffset;

                    sr.Endian = Syroot.BinaryData.Core.Endian.Big;
                    int parentNode = sr.ReadInt32();
                    sr.Endian = Syroot.BinaryData.Core.Endian.Little;

                    string str;
                    if (entryLen >= 4)
                        str = sr.ReadStringRaw(entryLen - 4);
                    else
                        str = "string was null";

                    sw.WriteLine($"{j} -> Offset: {entryOffset:X2} - Length: {entryLen} - Points to Page: {pageIndex} | ParentNode: {parentNode}, Name: {str}");
                }
                else
                {
                    short entryMetaTypeOffset = sr.ReadInt16();
                    short entryMetaTypeLen = sr.ReadInt16();
                    sr.Position = entryOffset;

                    sr.Endian = Syroot.BinaryData.Core.Endian.Big;
                    int parentNode = sr.ReadInt32();
                    sr.Endian = Syroot.BinaryData.Core.Endian.Little;

                    string str = sr.ReadStringRaw(entryLen - 4);

                    sw.WriteLine($"{j} -> Offset: {entryOffset:X2} - Length: {entryLen} - Data Offset: {entryMetaTypeOffset} - Data Len: {entryMetaTypeLen} | ParentNode: {parentNode}, Name: {str}");
                }
            }

            sw.WriteLine();
        }
    }

    public void VerifyPageIndices()
    {
        Console.WriteLine("Verifying pages.");
        VolumeStream.Position = TocOffset + 0x12;
        ushort pageCount = VolumeStream.ReadUInt16();

        
        PageInfo page = GetPage(0);
        PageDebugInfo pageDebInfo = new PageDebugInfo();
        pageDebInfo.ReadFromPageInfo(page);

        foreach (var entry in pageDebInfo.Entries)
        {
            var childIndexPage = GetPage(entry.PageIndex);
            PageDebugInfo childPageDebInfo = new PageDebugInfo();
            childPageDebInfo.ReadFromPageInfo(childIndexPage);

            for (int i = 0; i < childPageDebInfo.Entries.Count; i++)
            {
                PageDebugEntryInfo cutoff = childPageDebInfo.Entries[i];
                // Get the entries in the middle
                var prevEntryPage = GetPage(cutoff.PageIndex);
                PageDebugInfo prevEntryPageDebInfo = new PageDebugInfo();
                prevEntryPageDebInfo.ReadFromPageInfo(prevEntryPage);

                var nextEntryPage = GetPage(cutoff.PageIndex + 1);
                PageDebugInfo nextEntryPageDebInfo = new PageDebugInfo();
                nextEntryPageDebInfo.ReadFromPageInfo(nextEntryPage);


                var lastEntry = prevEntryPageDebInfo.Entries[^1];
                var nextEntry = nextEntryPageDebInfo.Entries[0];

                (byte[] nodeid, string lookup) result = CompareEntries(lastEntry, nextEntry);
                byte[] merged = result.nodeid.Concat(Encoding.UTF8.GetBytes(result.lookup)).ToArray();

                bool isEqual = cutoff.IndexData.AsSpan().SequenceEqual(merged);
                Debug.Assert(isEqual);

                if (i == childPageDebInfo.Entries.Count - 1)
                {
                    var nextMasterCutoff = GetPage(cutoff.PageIndex + 1 + 2);
                    PageDebugInfo nextMasterEntryPageDebInfo = new PageDebugInfo();
                    nextMasterEntryPageDebInfo.ReadFromPageInfo(nextMasterCutoff);

                    var prevMasterEntry = nextEntryPageDebInfo.Entries[^1];
                    var nextMasterEntry = nextMasterEntryPageDebInfo.Entries[0];

                    result = CompareEntries(prevMasterEntry, nextMasterEntry);
                    merged = result.nodeid.Concat(Encoding.UTF8.GetBytes(result.lookup)).ToArray();

                    isEqual = entry.IndexData.AsSpan().SequenceEqual(merged);
                    Debug.Assert(isEqual);
                }
            }
        }

        Console.WriteLine("Index pages are correctly linked.");

        (byte[] nodeid, string lookup) CompareEntries(PageDebugEntryInfo prevLastPageEntry, PageDebugEntryInfo nextFirstPageEntry)
        {
            byte[] res = new byte[4];
            if (prevLastPageEntry.ParentNode != nextFirstPageEntry.ParentNode)
            {
                byte[] prevNodeID = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(prevNodeID, prevLastPageEntry.ParentNode);

                byte[] nextNodeID = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(nextNodeID, nextFirstPageEntry.ParentNode);
                
                for (int i = 0; i < 4; i++)
                {
                    if (prevNodeID[i] != nextNodeID[i])
                    {
                        res = nextNodeID.AsSpan(0, i+1).ToArray();
                        break;
                    }
                }
                return (res, string.Empty); // No point returning a file name, the parent node is already enough of a difference
            }

            string lastName = prevLastPageEntry.Name;
            string firstNextName = nextFirstPageEntry.Name;

            int maxLen = Math.Max(lastName.Length, firstNextName.Length);
            BinaryPrimitives.WriteInt32BigEndian(res, nextFirstPageEntry.ParentNode);

            for (int i = 0; i < maxLen; i++)
            {
                if (i >= lastName.Length)
                    return (res, firstNextName.Substring(0, i + 1));

                if (lastName[i] != firstNextName[i])
                    return (res, firstNextName.Substring(0, i + 1));
            }

            // This is unpossible, or else both entries are the same file due to being the same parent
            throw new ArgumentException($"First entry is equal to the second entry. ({lastName}, parent ID {prevLastPageEntry.ParentNode})");
        }
    }

    public void Close()
        => VolumeStream?.Dispose();
}

public class PageInfo
{
    public DebugReader ParentVolume { get; set; }
    public int PageIndex { get; set; }
    public byte[] PageBuffer { get; set; }

    public bool IsIndexPage 
        => BinaryPrimitives.ReadInt16LittleEndian(PageBuffer) == 1;

    public void SwitchToNewIndex(int index)
    {
        if (PageIndex != -1)
            PageIndex = -1;

        PageBuffer = ParentVolume.GetPageBuffer(index);
        if (PageBuffer != null)
            PageIndex = index;
    }

    public int SearchIndexPage(byte[] input, int inputLen)
    {
        SpanReader sr = new SpanReader(PageBuffer);
        sr.Position = 2;

        int entryCountRaw = sr.ReadInt16();
        ushort pageSize = ParentVolume.GetPageSize();

        if (entryCountRaw == 1)
        {
            sr.Position = pageSize - 4;
            return sr.ReadByte() | sr.ReadByte() << 8 | sr.ReadByte() << 16 | sr.ReadByte() << 24;
        }

        // Check first
        sr.Position = pageSize - 0x08;
        short entryOffset = sr.ReadInt16();
        short entryLength = sr.ReadInt16();
        sr.Position = entryOffset;
        byte[] entryIndexer = sr.ReadBytes(entryLength);

        if (DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength) < 0)
        {
            sr.Position = pageSize - 4;
            return sr.ReadByte() | sr.ReadByte() << 8 | sr.ReadByte() << 16 | sr.ReadByte() << 24;
        }

        int min = 0;
        int max = (((entryCountRaw - 1) >> 1) - 1);

        // Check last
        sr.Position = pageSize - (max * 0x08) - 8;
        entryOffset = sr.ReadInt16();
        entryLength = sr.ReadInt16();
        sr.Position = entryOffset;
        entryIndexer = sr.ReadBytes(entryLength);

        if (DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength) > 0)
        {
            sr.Position = pageSize - (max * 0x08) - (8 + 4);
            return sr.ReadByte() | sr.ReadByte() << 8 | sr.ReadByte() << 16 | sr.ReadByte() << 24;
        }

        while ((max - min) >= 8)
        {
            int mid = (min + max) / 2;
            int entryItorOffset = pageSize - (mid * 0x08);

            sr.Position = entryItorOffset - 8;
            entryOffset = sr.ReadInt16();
            entryLength = sr.ReadInt16();

            sr.Position = entryOffset;
            entryIndexer = sr.ReadBytes(entryLength);

            int diff = DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength);
            if (diff == 0)
            {
                sr.Position = entryItorOffset - (8 + 4);
                return sr.ReadByte() | sr.ReadByte() << 8 | sr.ReadByte() << 16 | sr.ReadByte() << 24;
            }
            else if (diff > 0)
                min = mid;
            else
                max = mid;
        }

        // Last 8
        while (min <= max)
        {
            int entryItorOffset = pageSize - (min * 0x08);
            sr.Position = entryItorOffset - 8;
            entryOffset = sr.ReadInt16();
            entryLength = sr.ReadInt16();

            sr.Position = entryOffset;
            entryIndexer = sr.ReadBytes(entryLength);

            int diff = DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength);
            if (diff == 0)
            {
                sr.Position = entryItorOffset - (8 + 4);
                return sr.ReadByte() | sr.ReadByte() << 8 | sr.ReadByte() << 16 | sr.ReadByte() << 24;
            }

            if (diff < 0)
            {
                sr.Position = entryItorOffset - 4;
                return sr.ReadByte() | sr.ReadByte() << 8 | sr.ReadByte() << 16 | sr.ReadByte() << 24;
            }

            min++;
        }


        throw new Exception("Failed to bsearch the entry.");
        return -1;
    }

    public bool SearchEntry(byte[] input, int inputLen, out int indexResult)
    {
        indexResult = 0;

        SpanReader sr = new SpanReader(PageBuffer);
        sr.Position = 2;

        int entryCount = sr.ReadInt16() / 2;
        ushort pageSize = ParentVolume.GetPageSize();

        // Check first
        sr.Position = pageSize - 0x08;
        short entryOffset = sr.ReadInt16();
        short entryLength = sr.ReadInt16();
        sr.Position = entryOffset;
        byte[] entryIndexer = sr.ReadBytes(entryLength);

        int diff = DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength);
        if (diff < 0)
        {
            indexResult = 0;
            return false;
        }

        int min = 0;
        int max = entryCount - 1;

        if (max >= 8)
        {
            while (max - min >= 8)
            {
                int mid = (min + max) / 2;

                int baseOffset = pageSize - (mid * 0x08);
                sr.Position = baseOffset - 8;
                entryOffset = sr.ReadInt16();
                entryLength = sr.ReadInt16();

                sr.Position = entryOffset;
                entryIndexer = sr.ReadBytes(entryLength);

                diff = DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength);
                if (diff == 0)
                {
                    indexResult = mid;
                    return true;
                }
                else if (diff > 0)
                    min = mid;
                else
                    max = mid;
            }
        }

        while (min <= max)
        {
            int baseOffset = pageSize - (min * 0x08);
            sr.Position = baseOffset - 8;
            entryOffset = sr.ReadInt16();
            entryLength = sr.ReadInt16();

            sr.Position = entryOffset;
            entryIndexer = sr.ReadBytes(entryLength);
            diff = DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength);
            if (diff <= 0)
            {
                indexResult = min;
                return diff == 0;
            }

            min++;
        }
        
        throw new Exception("Failed to bsearch the entry.");
        return false;
    }
}

#region Non original classes, for debugging
public class PageDebugInfo
{
    public short PageType { get; set; }
    public short EntryCount { get; set; }
    public int NextPage { get; set; }
    public int PrevPage { get; set; }
    public bool IsMasterIndexPage { get; set; }
    public List<PageDebugEntryInfo> Entries { get; set; }

    public PageInfo Page { get; set; }

    public void ReadFromPageInfo(PageInfo page)
    {
        SpanReader sr = new SpanReader(page.PageBuffer);

        PageType = sr.ReadInt16();
        EntryCount = sr.ReadInt16();
        NextPage = sr.ReadInt32();
        PrevPage = sr.ReadInt32();
        Entries = new List<PageDebugEntryInfo>(EntryCount / 2);

        if (PageType == 1 && NextPage == -1 && PrevPage == -1)
            IsMasterIndexPage = true;

        for (int i = 0; i < EntryCount / 2; i++)
        {
            sr.Position = page.ParentVolume.PageSize - (i * 0x08) - 0x08;
            PageDebugEntryInfo entry = new PageDebugEntryInfo();
            entry.ReadFromBuffer(ref sr, PageType == 1);
            Entries.Add(entry);
        }
    }

}

public class PageDebugEntryInfo
{
    public short EntryOffset { get; set; }
    public short EntryLength { get; set; }
    public int PageIndex { get; set; }

    public byte[] IndexData { get; set; }

    public int ParentNode { get; set; }
    public string Name { get; set; }

    public void ReadFromBuffer(ref SpanReader sr, bool isIndexPage)
    {
        EntryOffset = sr.ReadInt16();
        EntryLength = sr.ReadInt16();
        if (isIndexPage)
            PageIndex = sr.ReadInt32();

        sr.Position = EntryOffset;

        if (isIndexPage)
            IndexData = sr.ReadBytes(EntryLength);
        else
        {
            sr.Endian = Syroot.BinaryData.Core.Endian.Big;
            ParentNode = sr.ReadInt32();
            sr.Endian = Syroot.BinaryData.Core.Endian.Little;
            Name = sr.ReadStringRaw(EntryLength - 4);
        }
    }
}
#endregion
