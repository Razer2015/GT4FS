// Used to create a testbed for creating a toc with a LOT of entries with multi-depth index pages
#if DEBUG
//#define TEST_MULTIPLE_INDICES_PAGES
#endif

using GT.Shared;
using GT.Shared.Helpers;

using GT4FS.Core.Entries;

using Syroot.BinaryData;
using Syroot.BinaryData.Core;
using Syroot.BinaryData.Memory;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Text;

namespace GT4FS.Core.Packing;

/// <summary>
/// Read-only file system builder for Gran Turismo 4 & related games.
/// </summary>
public class RoFSBuilder
{
    public const int ArbitraryLengthForToCToAvoidMerge = 0x2000000;

    /// <summary>
    /// Size of each page. Defaults to 0x800.
    /// </summary>
    public ushort PageSize { get; set; } = Volume.DEFAULT_PAGE_SIZE;

    /// <summary>
    /// The root of the folder structure - used to build the relationship between files.
    /// </summary>
    private DirEntry _rootTree { get; set; }

    // In-one-go entries of the whole file system for writing later on.
    private List<RecordEntry> _entriesToPack = [];

    // List of all TOC pages kept in memory to write the previous/next pages and page offsets later on.
    public List<PageBase> _pages = [];
    
    // For writing the entry's page offsets
    private int _baseDataOffset;

    /// <summary>
    /// Current node ID.
    /// </summary>
    private int _currentID = 1;

    public const int PageHeaderSize = 0x0C;

    public string InputFolder { get; set; }
    public uint BaseRealTocOffset { get; private set; }

    /// <summary>
    /// Whether to avoid merging the ToC and the volume contents by arbitrarily setting a very large toc page size to fit the toc.
    /// </summary>
    public bool NoMergeTocMode { get; private set; }

    public bool AppendToVolumeMode { get; private set; }

    /// <summary>
    /// For append mode
    /// </summary>
    public long BaseDataOffset { get; private set; }

    /// <summary>
    /// Whether or not to encrypt the volume header & toc. This is supported by the game.
    /// </summary>
    public bool Encrypt { get; private set; } = true;

    /// <summary>
    /// Whether or not to allow compressing files.
    /// </summary>
    public bool Compress { get; private set; } = true;

    public void SetEncrypted(bool encrypted)
       => Encrypt = encrypted;

    public void SetAppendMode(bool appendMode, long baseDataOffset = -1)
    {
        AppendToVolumeMode = appendMode;
        NoMergeTocMode = appendMode;
        BaseDataOffset = baseDataOffset;
    }

    public void SetNoMergeTocMode(bool noMerge)
    {
        NoMergeTocMode = noMerge;
    }

    public void SetCompressed(bool compress)
       => Compress = compress;

    public void SetPageSize(ushort pageSize)
        => PageSize = pageSize;

    /// <summary>
    /// Registers all the files to pack in volume scratch building mode.
    /// </summary>
    /// <param name="inputFolder"></param>
    public void RegisterFilesToPack(string inputFolder)
    {
        Console.WriteLine($"Indexing '{Path.GetFullPath(inputFolder)}' to find files to pack.. ");
        InputFolder = Path.GetFullPath(inputFolder);

        _rootTree = new DirEntry(".");
        _rootTree.NodeID = _currentID++;

        _entriesToPack.Add(_rootTree);

        ImportFolder(_rootTree, InputFolder, InputFolder);
        TraverseBuildFileEntryList(_entriesToPack, _rootTree);

        Console.WriteLine($"Found {_entriesToPack.Count(e => e.EntryType != RecordType.Directory)} files to pack.");
    }

    /// <summary>
    /// Registers all the files to pack in volume appending mode.
    /// </summary>
    /// <param name="originalBTree"></param>
    /// <param name="appendFilesPath"></param>
    public void RegisterFilesFromBTree(BTree originalBTree, string appendFilesPath)
    {
        var tocNodes = originalBTree.GetNodes().ToList();
        _rootTree = tocNodes[0] as DirEntry;

        // Build file tree of our mod path
        var appendModTree = new DirEntry(".");
        appendModTree.NodeID = _currentID++;

        List<RecordEntry> flattenedTree = [];
        flattenedTree.Add(appendModTree);
        ImportFolder(appendModTree, appendFilesPath, appendFilesPath);
        TraverseBuildFileEntryList(flattenedTree, appendModTree);

        // Merge the trees
        MergeDirEntries(_rootTree, appendModTree, "");

        // Reassign IDs for the combined tree
        _currentID = 1;
        RelinkDirIDs(_rootTree);

        // Build the final flattened list
        _entriesToPack.Add(_rootTree);
        TraverseBuildFileEntryList(_entriesToPack, _rootTree);
    }

    /// <summary>
    /// Merges two trees together (used for append mode).
    /// </summary>
    /// <param name="parentTocEntry"></param>
    /// <param name="parentAppendDirEntry"></param>
    /// <param name="volPath"></param>
    private void MergeDirEntries(DirEntry parentTocEntry, DirEntry parentAppendDirEntry, string volPath)
    {
        foreach (var entryToAppend in parentAppendDirEntry.ChildEntries)
        {
            // Path exists in 
            if (parentTocEntry.ChildEntries.TryGetValue(entryToAppend.Key, out RecordEntry tocEntry))
            {
                if (tocEntry.EntryType == RecordType.Directory)
                {
                    MergeDirEntries(tocEntry as DirEntry, entryToAppend.Value as DirEntry, volPath + tocEntry.Name + "/");
                }
                else
                {
                    // File already exists in toc, we are overwriting it
                    parentTocEntry.ChildEntries[entryToAppend.Key] = entryToAppend.Value;
                    entryToAppend.Value.IsModFileAppendToVolumeEnd = true;
                    Console.WriteLine($"Overwriting file - {volPath + entryToAppend.Value.Name}");
                }
            }
            else
            {
                // New file or directory
                // Children will be automatically re-ordered by the sorted dictionary
                if (entryToAppend.Value is not DirEntry)
                {
                    entryToAppend.Value.IsModFileAppendToVolumeEnd = true;
                }
                else
                {
                    tocEntry = new DirEntry();
                    tocEntry.Name = entryToAppend.Value.Name;
                    MergeDirEntries(tocEntry as DirEntry, entryToAppend.Value as DirEntry, volPath + tocEntry.Name + "/");
                }

                parentTocEntry.ChildEntries.Add(entryToAppend.Key, entryToAppend.Value);
            }
        }
    }

    /// <summary>
    /// Refreshes and reestablishes node ids & parent nodes (used for append mode).
    /// </summary>
    /// <param name="parentEntry"></param>
    private void RelinkDirIDs(DirEntry parentEntry)
    {
        foreach (var entry in parentEntry.ChildEntries)
        {
            if (entry.Value.EntryType == RecordType.Directory)
            {
                entry.Value.NodeID = ++_currentID;
                RelinkDirIDs((DirEntry)entry.Value);
            }
            else
            {
                entry.Value.NodeID = ++_currentID;
            }

            entry.Value.ParentNode = parentEntry.NodeID;
        }
    }

    /// <summary>
    /// Builds the volume.
    /// </summary>
    /// <param name="outputFile"></param>
    public void Build(string outputFile, uint baseRealTocOffset)
    {
        if (!AppendToVolumeMode)
            Console.WriteLine("Building volume.");
        else
            Console.WriteLine("Appending files to volume.");

        BaseRealTocOffset = baseRealTocOffset;

        BinaryStream volStream;
        if (AppendToVolumeMode)
        {
            var fs = File.Open(outputFile, FileMode.Open, FileAccess.ReadWrite);
            volStream = new BinaryStream(fs);
        }
        else
        {
            var fs = File.Open(outputFile, FileMode.Create);
            volStream = new BinaryStream(fs);
        }

        BuildVolumeToCAndContents(volStream);

        if (AppendToVolumeMode)
            Console.WriteLine("Done. Files appended to existing volume and ToC rewritten.");
        else
            Console.WriteLine($"Done, folder packed to {Path.GetFullPath(outputFile)}.");

        volStream.Dispose();
    }

    private static void WriteFakeToC(BinaryStream volStream)
    {
        volStream.WriteInt32(TocHeader.MagicValueEncrypted, ByteConverter.Big);

        // 2.2
        volStream.WriteInt16(2); // Version Minor
        volStream.WriteInt16(2); // Version Major
    }


    /// <summary>
    /// Builds the real table of contents and links the data with it.
    /// </summary>
    /// <param name="volStream"></param>
    private void BuildVolumeToCAndContents(BinaryStream volStream)
    {
        // Write fake 2.2 TOC. Polyphony wrote a fake toc to make people think GT3 tools worked on it.
        // The start offset is constant and writen into the executables as a page offset.
        WriteFakeToC(volStream);

        // Skip the page toc as it can't be written for now
        volStream.BaseStream.Position = BaseRealTocOffset + TocHeader.HeaderSize;

        try
        {
            if (!AppendToVolumeMode)
            {
                Console.WriteLine("Writing files into new volume.");

                // Start writing the files to our new scratch VOL to a seperate file that will be merged to the toc later on.
                WriteVolumeContents(volStream);
            }
            else
            {
                // Move files that need to be moved because of the general ToC page space pushed forwards.
                MoveFilesOverlappingOldTocPagesForAppendMode(volStream);

                // Process files that we are overwritting or adding to the volume.
                WriteAppendingFiles(volStream);
            }

            Console.WriteLine("Done writing files. Writing paged b-tree for table of contents..");
            // Write all the file & directory entries
            BuildToCPages();

            // We've got enough to build the header and merge pages together now
            WriteToCHeader(volStream);

            if (!NoMergeTocMode)
            {
                // Merge toc and file blob.
                using var fs = new FileStream("gtfiles.temp", FileMode.Open);
                Console.WriteLine($"Merging Data and ToC... ({Utils.BytesToString(fs.Length)})");

                int count = 0;
                byte[] buffer = new byte[0x200000];
                while ((count = fs.Read(buffer, 0, buffer.Length)) > 0)
                    volStream.BaseStream.Write(buffer, 0, count);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"An error occured while building volume: {e}");
        }
        finally
        {
            if (File.Exists("gtfiles.temp"))
                File.Delete("gtfiles.temp");
        }
    }

    /// <summary>
    /// For append mode
    /// </summary>
    /// <param name="volStream"></param>
    private void MoveFilesOverlappingOldTocPagesForAppendMode(BinaryStream volStream)
    {
        foreach (var entry in _entriesToPack)
        {
            if (entry.IsModFileAppendToVolumeEnd)
                continue; // We'll do these after

            long fileOffset, fileSize;
            if (entry.EntryType == RecordType.CompressedFile)
            {
                var compFile = entry as CompressedFileEntry;
                fileOffset = (long)compFile.PageOffset * PageSize;
                fileSize = compFile.CompressedSize;
            }
            else if (entry.EntryType == RecordType.File)
            {
                var fileEntry = entry as FileEntry;
                fileOffset = (long)fileEntry.PageOffset * PageSize;
                fileSize = fileEntry.Size;
            }
            else
                continue;

            int filePageOffset;
            if (_baseDataOffset + fileOffset < ArbitraryLengthForToCToAvoidMerge)
            {
                Console.WriteLine($"Moving '{entry.Name}' to end of volume to avoid overlapping with new ToC.");

                // This file is behind the pre-allocated space required for the toc, move it to end of volume
                volStream.BaseStream.Position = BaseDataOffset + fileOffset;
                byte[] buffer = new byte[fileSize];
                volStream.ReadExactly(buffer);
                volStream.Position = volStream.Length;

                filePageOffset = (int)((volStream.Length - ArbitraryLengthForToCToAvoidMerge) / PageSize);

                volStream.Write(buffer, 0, buffer.Length);
                volStream.Align(PageSize, grow: true);
            }
            else
            {
                // Adjust file's page offset
                filePageOffset = (int)((fileOffset - (ArbitraryLengthForToCToAvoidMerge - BaseDataOffset)) / PageSize);
            }

            if (entry.EntryType == RecordType.CompressedFile)
                (entry as CompressedFileEntry).PageOffset = filePageOffset;
            else if (entry.EntryType == RecordType.File)
                (entry as FileEntry).PageOffset = filePageOffset;
        }
    }

    private void WriteAppendingFiles(BinaryStream bs)
    {
        bs.Position = bs.Length;
        foreach (var entry in _entriesToPack)
        {
            if (!entry.IsModFileAppendToVolumeEnd)
                continue;

            var m = _entriesToPack.ToList().Where(e => e is FileEntry).Max(e => (e as FileEntry).PageOffset);
            using var file = File.Open(entry.AbsolutePath, FileMode.Open);
            if (entry.EntryType == RecordType.CompressedFile)
            {
                CompressedFileEntry compFile = entry as CompressedFileEntry;
                Console.WriteLine($"Compressing at volume end: {entry.VolumePath} [{Utils.BytesToString(compFile.Size)}]");

                compFile.PageOffset = (int)((bs.BaseStream.Length - ArbitraryLengthForToCToAvoidMerge) / PageSize);
                long compressedSize = Compression.PS2ZIPCompressInto(file, bs);
                compFile.CompressedSize = (int)compressedSize;
            }
            else
            {
                Console.WriteLine($"Writing at volume end: {entry.VolumePath} [{Utils.BytesToString(((FileEntry)entry).Size)}]");

                ((FileEntry)entry).PageOffset = (int)((bs.BaseStream.Length - ArbitraryLengthForToCToAvoidMerge) / PageSize);
                file.CopyTo(bs);
            }

            bs.Align(PageSize, grow: true);
        }
    }

    private void WriteToCHeader(BinaryStream volStream)
    {
        volStream.Position = BaseRealTocOffset + TocHeader.HeaderSize;
        uint pageCount = (uint)_pages.Count;

        // The game will subtract the current and next offset to 
        // determine the length of a page, thus the extra one will be the boundary
        volStream.Position += 4 * pageCount;
        volStream.Position += 4;

        // Actually write the toc pages.
        for (int i = 0; i < pageCount; i++)
        {
            uint pageOffset = (uint)(volStream.Position - BaseRealTocOffset);

            byte[] pageBuffer = new byte[Volume.DEFAULT_PAGE_SIZE];
            SpanWriter pageWriter = new SpanWriter(pageBuffer);
            _pages[i].Serialize(ref pageWriter);

            if (Encrypt)
                Utils.XorEncryptFast(pageBuffer, 0x55);
            var pageComp = PS2Zip.ZlibCodecCompress(pageBuffer);

            volStream.WriteBytes(pageComp);

            using (volStream.TemporarySeek(BaseRealTocOffset + TocHeader.HeaderSize + (i * 4), SeekOrigin.Begin))
                volStream.WriteUInt32(Encrypt ? EncryptOffset(pageOffset, (uint)i) : pageOffset);
        }

        uint tocLength = (uint)(volStream.Position - BaseRealTocOffset);

        if (NoMergeTocMode)
        {
            // Align to a really large offset so that our data offset is late - so we don't have to merge the data and toc
            volStream.Position = ArbitraryLengthForToCToAvoidMerge;
        }
        else
            volStream.Align(PageSize, grow: true);

        // Write the final offset
        _baseDataOffset = (int)(volStream.Position - BaseRealTocOffset);
        using (volStream.TemporarySeek(BaseRealTocOffset + TocHeader.HeaderSize + (pageCount * 4), SeekOrigin.Begin))
            volStream.WriteUInt32(Encrypt ? EncryptOffset(tocLength, pageCount) : tocLength);

        // Build main volume header.
        using (volStream.TemporarySeek(BaseRealTocOffset, SeekOrigin.Begin))
        {
            if (Encrypt)
                volStream.WriteInt32(TocHeader.MagicValueEncrypted, ByteConverter.Big);
            else
                volStream.WriteString(TocHeader.Magic, StringCoding.Raw);
            volStream.WriteInt32(TocHeader.Version3_1);

            uint tocPageCount = (uint)(_baseDataOffset / PageSize);
            Debug.Assert(tocPageCount <= ushort.MaxValue, "ToC Page count is too large - this volume likely has too many files or folders.");

            volStream.WriteUInt32(tocLength);
            volStream.WriteUInt32(tocPageCount);
            volStream.WriteUInt16(PageSize);
            volStream.WriteUInt16((ushort)pageCount);
        }
    }

    private void WriteVolumeContents(BinaryStream volStream)
    {
        BinaryStream bs;
        if (!NoMergeTocMode)
        {
            Console.WriteLine("Writing temporary contents file to merge with ToC later.");

            // Traditional, accurate RoFS building where the data is immediately after the ToC.
            // Therefore, it needs to be merged.
            var fs = new FileStream("gtfiles.temp", FileMode.Create);
            bs = new BinaryStream(fs);
        }
        else
        {
            Console.WriteLine("Not merging ToC and data (merge mode enabled)");

            // "Hack" where we set the ToC pretty far so that we don't have to merge the ToC and data
            bs = volStream;
            volStream.BaseStream.Position = ArbitraryLengthForToCToAvoidMerge;
        }

        int i = 1;
        int count = _entriesToPack.Count(c => c.EntryType != RecordType.Directory);

        WriteDirectory(bs, _rootTree, "", volStream.BaseStream.Position, ref i, ref count);

        if (!NoMergeTocMode)
            bs.Dispose(); // Clean up temp file
    }

    private void WriteDirectory(BinaryStream fileWriter, DirEntry parentDir, string path, long baseDataPos, ref int currentIndex, ref int count)
    {
        foreach (var entryKV in parentDir.ChildEntries)
        {
            var entry = entryKV.Value;
            if (entry.EntryType == RecordType.Directory)
            {
                string subPath = string.IsNullOrEmpty(path) ? entry.Name : $"{path}/{entry.Name}";
                WriteDirectory(fileWriter, (DirEntry)entry, subPath, baseDataPos, ref currentIndex, ref count);
            }
            else
            {
                string filePath = string.IsNullOrEmpty(path) ? entry.Name : $"{path}/{entry.Name}";
                int entrySize = 0;
                if (entry.EntryType == RecordType.File)
                {
                    entrySize = ((FileEntry)entry).Size;
                    ((FileEntry)entry).PageOffset = (int)Math.Round((double)((fileWriter.Position) / PageSize), MidpointRounding.AwayFromZero);
                }
                else if (entry.EntryType == RecordType.CompressedFile)
                {
                    entrySize = ((CompressedFileEntry)entry).Size;
                    ((CompressedFileEntry)entry).PageOffset = (int)Math.Round((double)((fileWriter.Position) / PageSize), MidpointRounding.AwayFromZero);
                }

#if TEST_MULTIPLE_INDICES_PAGES
                if (!File.Exists(Path.Combine(InputFolder, filePath)))
                    continue;
#endif

                using var file = File.Open(Path.Combine(InputFolder, filePath), FileMode.Open);
                long fileSize = file.Length;

                // Not sure if the volume supports uint, but ArrayPool only supports int at least
                if (file.Length >= int.MaxValue)
                    throw new Exception($"File {filePath} is too large.");

                if (entry.EntryType == RecordType.CompressedFile)
                {
                    if (fileSize >= 1_024_000 || currentIndex % 100 == 0)
                        Console.WriteLine($"Compressing: {filePath} [{Utils.BytesToString(fileSize)}] ({currentIndex}/{count})");
                    long compressedSize = Compression.PS2ZIPCompressInto(file, fileWriter);

                    ((CompressedFileEntry)entry).CompressedSize = (int)compressedSize;
                    if (compressedSize >= fileSize)
                        Console.WriteLine($"Note: {filePath} is not compressing well, may already be compressed or encrypted (size: {fileSize:X8}, compressed: {compressedSize:X8})");
                }
                else
                {
                    if (fileSize >= 1_024_000 || currentIndex % 100 == 0)
                        Console.WriteLine($"Writing: {filePath} [{Utils.BytesToString(fileSize)}] ({currentIndex}/{count})");
                    file.CopyTo(fileWriter);
                }

                fileWriter.Align(PageSize, grow: true);
                currentIndex++;
            }
        }
    }

    private static uint EncryptOffset(uint offset, uint index)
        => offset ^ index * Volume.OffsetCryptKey + Volume.OffsetCryptKey;

    /// <summary>
    /// Serializes all the entries into toc data page.
    /// </summary>
    /// <returns></returns>
    private void BuildToCPages()
    {
        var currentIndexPage = new IndexPage(PageSize);
        var currentRecordPage = new RecordPage(PageSize);

        List<IndexPage> indexPages = [];

        // Used to keep track of where the last index page is so we add it 
        // before the new entry pages, every time
        int lastIndexPagePos = 0;

        int currentEntryIndex = 0;
        RecordEntry currentRecord;
        while (currentEntryIndex < _entriesToPack.Count)
        {
            currentRecord = _entriesToPack[currentEntryIndex];

            // Write to current record page
            if (!currentRecordPage.TryAddEntry(currentRecord))
            {
                // We couldn't fit it, we create a new one
                RecordEntry lastRecord = _entriesToPack[currentEntryIndex - 1];
                currentRecordPage.LastEntry = lastRecord;

                // insert it in the index page if we can
                RegisterNewIndexForRecordPages(lastRecord, currentRecord, ref currentIndexPage, currentRecordPage, indexPages, ref lastIndexPagePos);

                currentRecordPage.PageIndex = _pages.Count;
                _pages.Add(currentRecordPage);

                var newRecordPage = new RecordPage(PageSize);
                newRecordPage.PreviousPage = currentRecordPage;
                currentRecordPage.NextPage = newRecordPage;
                currentRecordPage = newRecordPage;
                currentRecordPage.TryAddEntry(currentRecord);
            }

            if (currentEntryIndex == _entriesToPack.Count - 1)
            {
                currentRecordPage.LastEntry = _entriesToPack[currentEntryIndex - 1];

                if (currentIndexPage.Entries.Count == 0)
                {
                    _pages.Remove(currentIndexPage);
                    indexPages.Remove(currentIndexPage);
                    currentIndexPage = null;
                }
                else if (!_pages.Contains(currentIndexPage))
                {
                    InsertIndexPageAndReadjustIndices(currentIndexPage, indexPages, lastIndexPagePos);
                }
            }

            currentEntryIndex++;
        }

        // If nothing was writen in the new pages, just discard them
        if (currentRecordPage.Entries.Count == 0)
        {
            _pages.Remove(currentRecordPage);
            currentRecordPage = null;
        }

        // Ensure to add the last record page if it hasn't been already.
        if (currentRecordPage is not null && !_pages.Contains(currentRecordPage))
        {
            var lastRecord = _entriesToPack[currentEntryIndex - 2];
            currentRecord = _entriesToPack[currentEntryIndex - 1];
            currentRecordPage.LastEntry = lastRecord;
            RegisterNewIndexForRecordPages(lastRecord, currentRecord, ref currentIndexPage, currentRecordPage, indexPages, ref lastIndexPagePos);

            currentRecordPage.PageIndex = _pages.Count;
            _pages.Add(currentRecordPage);
        }

        // Create index pages that links other index pages.
        List<IndexPage> currentIndexPages = indexPages;
        while (currentIndexPages.Count > 1)
        {
            var parentIndexPages = new List<IndexPage>();
            var currentParentIndexPage = new IndexPage(PageSize);
            parentIndexPages.Add(currentParentIndexPage);

            for (int i = 1; i < currentIndexPages.Count; i++)
            {
                IndexPage prevIndexPage = currentIndexPages[i - 1];
                IndexPage nextIndexPage = currentIndexPages[i];

                if (!currentParentIndexPage.TryAddEntry(prevIndexPage.LastPage.Entries[^1], prevIndexPage.LastPage.NextPage.Entries[0], prevIndexPage))
                {
                    var newParentIndexPage = new IndexPage(PageSize);
                    currentParentIndexPage.NextPage = newParentIndexPage;
                    newParentIndexPage.PreviousPage = currentParentIndexPage;
                    parentIndexPages.Add(newParentIndexPage);

                    currentParentIndexPage = newParentIndexPage;
                    currentParentIndexPage.TryAddEntry(prevIndexPage.LastPage.Entries[^1], prevIndexPage.LastPage.NextPage.Entries[0], prevIndexPage);
                }

                currentParentIndexPage.LastPage = nextIndexPage;
            }

            for (int i = 0; i < parentIndexPages.Count; i++)
                parentIndexPages[i].PageIndex = i;

            for (int i = 0; i < _pages.Count; i++)
                _pages[i].PageIndex += parentIndexPages.Count;
            _pages.InsertRange(0, parentIndexPages);

            currentIndexPages = parentIndexPages;
        }

        // Some base index pages may have an extra entry at the bottom (which is covered by the terminator), so remove it.
        foreach (var page in _pages)
        {
            if (page is not IndexPage indexPage)
                continue;

            var lastEntry = indexPage.Entries[^1] as IndexEntry;
            if (indexPage.LastPage.PageIndex == lastEntry.SubPageRef.PageIndex)
                indexPage.Entries.Remove(lastEntry);
        }

        // The last record page of each group should not link to a next page.
        for (int i = 0; i < indexPages.Count; i++)
            indexPages[i].LastPage.NextPage = null;
    }

    /// <summary>
    /// Adds a new index for two record pages.
    /// </summary>
    /// <param name="lastRecord">Last/previous record.</param>
    /// <param name="currentRecord">Current/next record.</param>
    /// <param name="currentIndexPage">Current index page to add to. May be changed if it doesn't fit in it.</param>
    /// <param name="currentRecordPage"></param>
    /// <param name="indexPages">Current index pages. May have an additional entry if it does not fit in the current index page.</param>
    /// <param name="lastIndexPageId">Index of the last index page.</param>
    private void RegisterNewIndexForRecordPages(RecordEntry lastRecord, RecordEntry currentRecord, ref IndexPage currentIndexPage, 
        RecordPage currentRecordPage, List<IndexPage> indexPages, ref int lastIndexPageId)
    {
        if (!currentIndexPage.TryAddEntry(lastRecord, currentRecord, currentRecordPage))
        {
            // Keep track of the cutoff
            currentIndexPage.PrevPageLastEntry = lastRecord;
            currentIndexPage.NextPageFirstEntry = currentRecord;
            InsertIndexPageAndReadjustIndices(currentIndexPage, indexPages, lastIndexPageId);

            var newIndexPage = new IndexPage(PageSize);
            newIndexPage.PreviousPage = currentIndexPage;
            currentIndexPage.NextPage = newIndexPage;
            currentIndexPage = newIndexPage;
            lastIndexPageId = _pages.Count;

            newIndexPage.TryAddEntry(lastRecord, currentRecord, currentRecordPage);
        }
        else
            currentIndexPage.LastPage = currentRecordPage;
    }

    private void InsertIndexPageAndReadjustIndices(IndexPage indexPage, List<IndexPage> indexPages, int indexToInsert)
    {
        indexPage.PageIndex = indexToInsert;
        _pages.Insert(indexToInsert, indexPage);
        for (int i = indexToInsert + 1; i < _pages.Count; i++)
            _pages[i].PageIndex++;

        indexPages.Add(indexPage);
    }

    /// <summary>
    /// Imports a local file directory as a game directory node.
    /// </summary>
    /// <param name="parent"></param>
    /// <param name="folder"></param>
    private void ImportFolder(DirEntry parent, string rootFolder, string folder)
    {
        string folderVolumePath;
        if (rootFolder == folder) // Root
            folderVolumePath = "";
        else
            folderVolumePath = folder.Substring(rootFolder.Length + 1);

        var dirEntries = Directory.EnumerateFileSystemEntries(folder)
            .OrderBy(e => e, StringComparer.Ordinal).ToList();

#if TEST_MULTIPLE_INDICES_PAGES
        if (rootFolder == folder)
        {
            for (int i = 0; i < 100000; i++)
            {
                dirEntries.Add(Path.Combine(folder, $"zz{i,-0x78:X8}"));
            }
        }
#endif

        foreach (var path in dirEntries)
        {
            if (path.EndsWith("extract.log"))
                continue;

            RecordEntry entry;

            string relativePath = path.Substring(folder.Length + 1);
            string absolutePath = Path.Combine(folder, relativePath);
            string entryVolumePath = absolutePath.Substring(rootFolder.Length + 1);

#if TEST_MULTIPLE_INDICES_PAGES
            if (path.Contains("zz000"))
            {
                entry = new FileEntry($"z{Path.GetFileName(path),-0x78:X8}");
                entry.AbsolutePath = absolutePath;
                entry.VolumePath = entryVolumePath;
                entry.NodeID = _currentID++;
            }
            else
            {
#endif
                if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
                {
                    entry = new DirEntry(relativePath);
                    entry.NodeID = _currentID++;
                    ImportFolder((DirEntry)entry, rootFolder, path);
                }
                else
                {
                    var fInfo = new FileInfo(absolutePath);
                    if (Compress && IsNormallyCompressedVolumeFile(entryVolumePath))
                    {
                        entry = new CompressedFileEntry(relativePath);
                        ((CompressedFileEntry)entry).Size = (int)fInfo.Length;
                        ((CompressedFileEntry)entry).ModifiedDate = fInfo.LastWriteTimeUtc;
                    }
                    else
                    {
                        entry = new FileEntry(relativePath);
                        ((FileEntry)entry).Size = (int)fInfo.Length;
                        ((FileEntry)entry).ModifiedDate = fInfo.LastWriteTimeUtc;
                    }

                    entry.AbsolutePath = absolutePath;
                    entry.VolumePath = entryVolumePath;

                    entry.NodeID = _currentID++;
                }
#if TEST_MULTIPLE_INDICES_PAGES
            }
#endif

            entry.ParentNode = parent.NodeID;
            parent.ChildEntries.Add(entry.Name, entry);
        }
    }

    private bool IsNormallyCompressedVolumeFile(string file)
    {
        // Main folders that arent compressed - GT4
        if (file.StartsWith("bgm") || file.StartsWith("cameras")
            || file.StartsWith("description") || file.StartsWith("icon")
            || file.StartsWith("music") || file.StartsWith("printer") || file.StartsWith("sound") || file.StartsWith("text"))
            return false;

        // GT4/TT
        if (file.StartsWith("carsound") || file.StartsWith("motosound"))
            return false;

        // GT4O
        if (file.StartsWith("dnas"))
            return false;

        // GTHD
        if (file.StartsWith("movie") || file.StartsWith("rtext") || file.StartsWith("sound_gt"))
            return false;

        // Prize models are uncompressed
        if (file.StartsWith("piece/prize"))
            return false;

        // US.rtt?
        if (file.EndsWith(".rtt"))
            return false;

        if (file.EndsWith(".mproject"))
            return false;

        // Movie files, already compressed
        if (file.EndsWith(".ipic") || file.EndsWith(".pss"))
            return false;

        // Some images
        if (file.EndsWith(".ico") || file.EndsWith(".tga"))
            return false;

        // Audio files, already compressed
        if (file.EndsWith(".adm") || file.EndsWith(".ads") || file.EndsWith(".ins") || file.EndsWith(".sqt") || 
            file.EndsWith(".raw") || file.EndsWith(".inf") || file.EndsWith(".es") || file.StartsWith("sdvol.dat"))
            return false;

        if (file.StartsWith("text/realtime.dat"))
            return false;

        if (file.StartsWith("crs/bestline"))
            return false;

        if (file.StartsWith("font/jis2uni.dat"))
            return false;

        // Some gpbs in adhoc projects
        if (file.StartsWith("projects")) 
        {
            if (file.Contains("eyetoy") || file.Contains("language") || 
                file.Contains("quick") || file.Contains("quick-arcade") ||
                file.Contains("option") || file.Contains("gtmode"))
                return !file.EndsWith(".gpb");

            return true;
        }

        if (file.StartsWith("menu/select/tire.mdl"))
            return false;

        if (file.StartsWith("menu/pause") || file.StartsWith("menu/replay_panel"))
            return !file.EndsWith(".pmb");

        return true;
    }

    /// <summary>
    /// Builds the 2D representation of the file system, for packing.
    /// </summary>
    /// <param name="parentDir"></param>
    private void TraverseBuildFileEntryList(List<RecordEntry> entries, DirEntry parentDir)
    {
        foreach (var entry in parentDir.ChildEntries)
            entries.Add(entry.Value);

        foreach (var entry in parentDir.ChildEntries)
        {
            if (entry.Value is DirEntry childDir)
                TraverseBuildFileEntryList(entries, childDir);
        }
    }

    public static uint GetRealToCOffsetForGame(GameVolumeType game)
    {
        return game switch
        {
            GameVolumeType.GTHD => 0x1 * Volume.DEFAULT_PAGE_SIZE,

            // From this point on, 17mb+ of wasted space..
            GameVolumeType.TT => 0x2231 * Volume.DEFAULT_PAGE_SIZE,
            GameVolumeType.TT_DEMO => 0x2159 * Volume.DEFAULT_PAGE_SIZE,
            GameVolumeType.GT4 => 0x2159 * Volume.DEFAULT_PAGE_SIZE,
            GameVolumeType.GT4_MX5_DEMO => 0x2159 * Volume.DEFAULT_PAGE_SIZE,
            GameVolumeType.GT4_FIRST_PREV => 0x2159 * Volume.DEFAULT_PAGE_SIZE,
            GameVolumeType.GT4_ONLINE => 0x22B7 * Volume.DEFAULT_PAGE_SIZE,
            _ => 0x800,
        };
    }
}
