using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.IO;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

using Syroot.BinaryData;
using Syroot.BinaryData.Core;
using Syroot.BinaryData.Memory;

using GT.Shared;
using GT.Shared.Helpers;
using GT4FS.Core;
using GT4FS.Core.Entries;

namespace GT4FS.Core.Packing
{
    /// <summary>
    /// Read-only file system builder for Gran Turismo 4 & related games.
    /// </summary>
    public class RoFSBuilder
    {



        public const int ArbitraryLengthForToCToAvoidMerge = 0x2000000;

        /// <summary>
        /// Size of each page. Defaults to 0x800.
        /// </summary>
        public int PageSize { get; set; } = Volume.DefaultPageSize;

        /// <summary>
        /// The root of the folder structure - used to build the relationship between files.
        /// </summary>
        private DirEntry _rootTree { get; set; }

        // In-one-go entries of the whole file system for writing later on.
        private List<Entry> _entriesToPack = new List<Entry>();

        // To keep track of which entry is currently being saved
        private int _currentEntry = 0;

        public IndexPage CurrentIndexPage { get; set; }
        public EntryDescriptorPage CurrentEntryPage { get; set; }

        // List of all TOC pages kept in memory to write the previous/next pages and page offsets later on.
        public List<PageBase> _pages = new List<PageBase>();
        public List<IndexPage> _indexPage = new List<IndexPage>();
        
        // If theres more than one index page, we need a main one that links to them
        private IndexPage _mainIndexPage { get; set; }

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

            Console.WriteLine($"Found {_entriesToPack.Count(e => e.EntryType != VolumeEntryType.Directory)} files to pack.");
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

            var flattenedTree = new List<Entry>();
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
                if (parentTocEntry.ChildEntries.TryGetValue(entryToAppend.Key, out Entry tocEntry))
                {
                    if (tocEntry.EntryType == VolumeEntryType.Directory)
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
                        entryToAppend.Value.IsModFileAppendToVolumeEnd = true;

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
                if (entry.Value.EntryType == VolumeEntryType.Directory)
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

            if (!AppendToVolumeMode)
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
                if (entry.EntryType == VolumeEntryType.CompressedFile)
                {
                    var compFile = entry as CompressedFileEntry;
                    fileOffset = (long)compFile.PageOffset * PageSize;
                    fileSize = compFile.CompressedSize;
                }
                else if (entry.EntryType == VolumeEntryType.File)
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
                    volStream.Read(buffer);
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

                if (entry.EntryType == VolumeEntryType.CompressedFile)
                    (entry as CompressedFileEntry).PageOffset = filePageOffset;
                else if (entry.EntryType == VolumeEntryType.File)
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
                if (entry.EntryType == VolumeEntryType.CompressedFile)
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
            int pageCount = _pages.Count;

            // The game will subtract the current and next offset to 
            // determine the length of a page, thus the extra one will be the boundary
            volStream.Position += 4 * pageCount;
            volStream.Position += 4;

            // Actually write the toc pages.
            for (int i = 0; i < pageCount; i++)
            {
                int pageOffset = (int)(volStream.Position - BaseRealTocOffset);

                byte[] page = _pages[i].Buffer;
                byte[] copy = new byte[page.Length];
                Span<byte> copySpan = copy.AsSpan(0, page.Length);
                page.AsSpan().CopyTo(copySpan);

                if (Encrypt)
                    Utils.XorEncryptFast(copySpan, 0x55);
                var pageComp = PS2Zip.ZlibCodecCompress(copy);

                volStream.WriteBytes(pageComp);

                using (volStream.TemporarySeek(BaseRealTocOffset + TocHeader.HeaderSize + (i * 4), SeekOrigin.Begin))
                    volStream.WriteInt32(Encrypt ? EncryptOffset(pageOffset, i) : pageOffset);
            }

            int tocLength = (int)(volStream.Position - BaseRealTocOffset);


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
                volStream.WriteInt32(Encrypt ? EncryptOffset(tocLength, pageCount) : tocLength);

            // Build main volume header.
            using (volStream.TemporarySeek(BaseRealTocOffset, SeekOrigin.Begin))
            {
                if (Encrypt)
                    volStream.WriteInt32(TocHeader.MagicValueEncrypted, ByteConverter.Big);
                else
                    volStream.WriteString(TocHeader.Magic, StringCoding.Raw);
                volStream.WriteInt32(TocHeader.Version3_1);

                int tocPageCount = _baseDataOffset / PageSize;
                volStream.WriteInt32(tocLength);
                volStream.WriteInt32(tocPageCount);
                volStream.WriteUInt16((ushort)PageSize);
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
            int count = _entriesToPack.Count(c => c.EntryType != VolumeEntryType.Directory);

            WriteDirectory(bs, _rootTree, "", volStream.BaseStream.Position, ref i, ref count);

            if (!NoMergeTocMode)
                bs.Dispose(); // Clean up temp file
        }

        private void WriteDirectory(BinaryStream fileWriter, DirEntry parentDir, string path, long baseDataPos, ref int currentIndex, ref int count)
        {
            foreach (var entryKV in parentDir.ChildEntries)
            {
                var entry = entryKV.Value;
                if (entry.EntryType == VolumeEntryType.Directory)
                {
                    string subPath = string.IsNullOrEmpty(path) ? entry.Name : $"{path}/{entry.Name}";
                    WriteDirectory(fileWriter, (DirEntry)entry, subPath, baseDataPos, ref currentIndex, ref count);
                }
                else
                {
                    string filePath = string.IsNullOrEmpty(path) ? entry.Name : $"{path}/{entry.Name}";

                    int entrySize = 0;
                    if (entry.EntryType == VolumeEntryType.File)
                    {
                        entrySize = ((FileEntry)entry).Size;
                        ((FileEntry)entry).PageOffset = (int)Math.Round((double)((fileWriter.Position - baseDataPos) / PageSize), MidpointRounding.AwayFromZero);
                    }
                    else if (entry.EntryType == VolumeEntryType.CompressedFile)
                    {
                        entrySize = ((CompressedFileEntry)entry).Size;
                        ((CompressedFileEntry)entry).PageOffset = (int)Math.Round((double)((fileWriter.Position - baseDataPos) / PageSize), MidpointRounding.AwayFromZero);
                    }

                    using var file = File.Open(Path.Combine(InputFolder, filePath), FileMode.Open);
                    long fileSize = file.Length;

                    if (entry.EntryType == VolumeEntryType.CompressedFile)
                    {
                        if (fileSize >= 1_024_000 || currentIndex % 100 == 0)
                            Console.WriteLine($"Compressing: {filePath} [{Utils.BytesToString(fileSize)}] ({currentIndex}/{count})");
                        long compressedSize = Compression.PS2ZIPCompressInto(file, fileWriter);

                        ((CompressedFileEntry)entry).CompressedSize = (int)compressedSize;
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

        private static int EncryptOffset(int offset, int index)
            => offset ^ index * Volume.OffsetCryptKey + Volume.OffsetCryptKey;

        /// <summary>
        /// Serializes all the entries into toc data page.
        /// </summary>
        /// <returns></returns>
        private void BuildToCPages()
        {
            CurrentIndexPage = new IndexPage(PageSize);
            CurrentEntryPage = new EntryDescriptorPage(PageSize);

            // Used to keep track of where the last index page is so we add it 
            // before the new entry pages, every time
            int lastIndexPagePos = 0;

            Entry entry;
            while (_currentEntry < _entriesToPack.Count)
            {
                entry = _entriesToPack[_currentEntry];

                // Write to the next index page
                if (CurrentEntryPage.HasSpaceToWriteEntry(entry))
                    CurrentEntryPage.WriteEntry(entry);
                else
                {
                    CurrentEntryPage.FinalizeHeader();
                    CurrentEntryPage.LastEntry = _entriesToPack[_currentEntry - 1];

                    // Entry page has ran out of space - insert it in the index page if we can
                    if (CurrentIndexPage.HasSpaceToWriteEntry(_entriesToPack[_currentEntry - 1], entry))
                        CurrentIndexPage.WriteNextDataEntry(_entriesToPack[_currentEntry - 1], entry);
                    else
                    {
                        // Dirty hack - we don't need the last one
                        CurrentIndexPage.EntryCount--;

                        // Index also ran out of space? Well new one
                        CurrentIndexPage.FinalizeHeader();

                        // Keep track of the cutoff
                        CurrentIndexPage.PrevPageLastEntry = _entriesToPack[_currentEntry - 1];
                        CurrentIndexPage.NextPageFirstEntry = entry;

                        _pages.Insert(lastIndexPagePos, CurrentIndexPage);
                        _indexPage.Add(CurrentIndexPage);

                        var newIndexPage = new IndexPage(PageSize);
                        newIndexPage.PreviousPage = CurrentIndexPage;
                        CurrentIndexPage.NextPage = newIndexPage;
                        CurrentIndexPage = newIndexPage;
                        lastIndexPagePos = _pages.Count;

                        CurrentIndexPage.WriteNextDataEntry(_entriesToPack[_currentEntry - 1], entry);
                    }

                    _pages.Add(CurrentEntryPage);
                    var newEntryPage = new EntryDescriptorPage(PageSize);
                    newEntryPage.PreviousPage = CurrentEntryPage;
                    CurrentEntryPage.NextPage = newEntryPage;
                    CurrentEntryPage = newEntryPage;
                    CurrentEntryPage.WriteEntry(entry);
                }

                if (_currentEntry == _entriesToPack.Count - 1)
                {
                    CurrentEntryPage.LastEntry = _entriesToPack[_currentEntry - 1];

                    if (CurrentIndexPage.EntryCount == 0)
                    {
                        _pages.Remove(CurrentIndexPage);
                        _indexPage.Remove(CurrentIndexPage);
                        CurrentIndexPage = null;
                    }
                    else if (!_pages.Contains(CurrentIndexPage))
                    {
                        _pages.Insert(lastIndexPagePos, CurrentIndexPage);
                        _indexPage.Add(CurrentIndexPage);
                    }

                    // If needed
                    CurrentEntryPage.FinalizeHeader();
                    CurrentIndexPage.FinalizeHeader();
                }

                _currentEntry++;
            }

            // If nothing was writen in the new pages, just discard them
            FinalizeToCPages();

            // Is there more than one index page? If so we may need to write a master one
            CreateMainIndexPageIfNeeded();

            // Link all the relational pages together
            AssignPageLinks();

            // Point index pages to their child page
            if (_mainIndexPage != null)
            {
                SpanWriter sw = new SpanWriter(_mainIndexPage.Buffer);

                // This will include the entry terminator we haven't written earlier
                for (int i = 0; i < _indexPage.Count; i++)
                {
                    var indexPage = _indexPage[i];
                    sw.Position = PageSize - ((i + 1) * 0x08);
                    sw.Position += 4;
                    sw.WriteInt32(indexPage.PageIndex);
                }
            }

            for (int i = 0; i < _indexPage.Count; i++)
            {
                var indexPage = _indexPage[i];
                int pageIndex = indexPage.PageIndex;

                SpanWriter sw = new SpanWriter(indexPage.Buffer);
                for (int j = 0; j < indexPage.EntryCount; j++)
                {
                    sw.Position = PageSize - ((j + 1) * 0x08);
                    sw.Position += 4;
                    sw.WriteInt32(pageIndex + 1 + j);
                }

                // Write last page terminator
                sw.Position = PageSize - ((indexPage.EntryCount + 1) * 0x08);
                sw.Position += 4;
                sw.WriteInt32(pageIndex + indexPage.EntryCount + 1);
            }
        }

        private void FinalizeToCPages()
        {
            // If nothing was writen in the last pages, just null them
            if (CurrentEntryPage.EntryCount == 0)
            {
                _pages.Remove(CurrentEntryPage);
                CurrentEntryPage = null;
            }
            else
            {
                if (!_pages.Contains(CurrentEntryPage))
                    _pages.Add(CurrentEntryPage);
            }
        }

        private void AssignPageIndices()
        {
            for (int i = 0; i < _pages.Count; i++)
                _pages[i].PageIndex = i;
        }

        private void CreateMainIndexPageIfNeeded()
        {
            if (_indexPage.Count > 1)
            {
                _mainIndexPage = new IndexPage(PageSize);
                _mainIndexPage.IsMasterPage = true;
                _pages.Insert(0, _mainIndexPage);

                AssignPageIndices();

                // We only need the middles.
                for (int i = 0; i < _indexPage.Count; i++)
                {                    
                    IndexPage indexPage = _indexPage[i];
                    var nextIndexPage = indexPage.NextPage as IndexPage;
                    if (nextIndexPage is null) 
                        break;

                    Entry lastPrev = (_pages[nextIndexPage.PageIndex - 1] as EntryDescriptorPage).LastEntry;

                    if (indexPage.PageIndex + 1 >= _pages.Count)
                        break;

                    Entry firstNext = (_pages[nextIndexPage.PageIndex + 1] as EntryDescriptorPage).FirstEntry;
                    _mainIndexPage.WriteNextDataEntry(lastPrev, firstNext);
                }

                _mainIndexPage.FinalizeHeader();
            }
            else
                AssignPageIndices();
        }

        private void AssignPageLinks()
        {
            for (int i = 0; i < _pages.Count; i++)
            {
                var page = _pages[i];
                if (page is IndexPage indexPage && indexPage.IsMasterPage)
                {
                    indexPage.WriteNextPage(-1);
                    indexPage.WritePreviousPage(-1);
                }
                else
                {
                    page.WriteNextPage(page.NextPage?.PageIndex ?? -1);
                    page.WritePreviousPage(page.PreviousPage?.PageIndex ?? -1);
                }
            }
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

            foreach (var path in dirEntries)
            {
                if (path.EndsWith("extract.log"))
                    continue;

                Entry entry;

                string relativePath = path.Substring(folder.Length + 1);
                string absolutePath = Path.Combine(folder, relativePath);
                string entryVolumePath = absolutePath.Substring(rootFolder.Length + 1);

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

                entry.ParentNode = parent.NodeID;
                parent.ChildEntries.Add(entry.Name, entry);
            }
        }

        private bool IsNormallyCompressedVolumeFile(string file)
        {
            // Main folders that arent compressed - GT4
            if (file.StartsWith("bgm") || file.StartsWith("cameras")
                || file.StartsWith("description") || file.StartsWith("dnas") || file.StartsWith("icon")
                || file.StartsWith("music") || file.StartsWith("printer") || file.StartsWith("sound") || file.StartsWith("text"))
                return false;

            // GTHD
            if (file.StartsWith("carsound") || file.StartsWith("movie") || file.StartsWith("rtext") || file.StartsWith("sound_gt"))
                return false;
            if (file.EndsWith(".mproject"))
                return false;

            // TT
            if (file.StartsWith("motosound") || (file.StartsWith("mpeg") && !file.EndsWith("course.ipic")))
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

            if (file.StartsWith("menu/pause") || file.StartsWith("menu/replay_panel"))
                return !file.EndsWith(".pmb");

            return true;
        }

        /// <summary>
        /// Builds the 2D representation of the file system, for packing.
        /// </summary>
        /// <param name="parentDir"></param>
        private void TraverseBuildFileEntryList(List<Entry> entries, DirEntry parentDir)
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
                GameVolumeType.GTHD => 0x1 * Volume.DefaultPageSize,

                // From this point on, 17mb+ of wasted space..
                GameVolumeType.TT => 0x2231 * Volume.DefaultPageSize,
                GameVolumeType.TT_DEMO => 0x2159 * Volume.DefaultPageSize,
                GameVolumeType.GT4 => 0x2159 * Volume.DefaultPageSize,
                GameVolumeType.GT4_MX5_DEMO => 0x2159 * Volume.DefaultPageSize,
                GameVolumeType.GT4_FIRST_PREV => 0x2159 * Volume.DefaultPageSize,
                GameVolumeType.GT4_ONLINE => 0x22B7 * Volume.DefaultPageSize,
                _ => 0x800,
            };
        }
    }
}
