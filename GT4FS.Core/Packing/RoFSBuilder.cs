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
        /// <summary>
        /// Whether or not to encrypt the volume header & toc. This is supported by the game.
        /// </summary>
        public bool Encrypt { get; set; } = true;

        /// <summary>
        /// Whether or not to allow compressing files.
        /// </summary>
        public bool Compress { get; set; } = true;

        public const int BlockHeaderSize = 0x0C;

        public string InputFolder { get; set; }
        public uint BaseRealTocOffset { get; set; }

        /// <summary>
        /// Size of each block. Defaults to 0x800.
        /// </summary>
        public int BlockSize { get; set; } = Volume.DefaultBlockSize;

        /// <summary>
        /// The root of the folder structure - used to build the relationship between files.
        /// </summary>
        public DirEntry RootTree { get; set; }

        // In-one-go entries of the whole file system for writing later on.
        private List<Entry> _entries = new List<Entry>();

        // To keep track of which entry is currently being saved
        private int _currentEntry = 0;

        public IndexBlock CurrentIndexBlock { get; set; }
        public EntryBlock CurrentEntryBlock { get; set; }

        // List of all TOC blocks kept in memory to write the previous/next pages and page offsets later on.
        public List<BlockBase> _blocks = new List<BlockBase>();
        public List<IndexBlock> _indexBlocks = new List<IndexBlock>();
        
        // If theres more than one index block, we need a main one that links to them
        private IndexBlock _mainIndexBlock { get; set; }

        // For writing the entry's page offsets
        private int _baseDataOffset;

        /// <summary>
        /// Current node ID.
        /// </summary>
        public int CurrentID = 1;

        public void RegisterFilesToPack(string inputFolder)
        {
            Console.WriteLine($"Indexing '{Path.GetFullPath(inputFolder)}' to find files to pack.. ");
            InputFolder = Path.GetFullPath(inputFolder);

            RootTree = new DirEntry(".");
            RootTree.NodeID = CurrentID++;

            _entries.Add(RootTree);

            Import(RootTree, InputFolder);
            TraverseBuildEntryPackList(RootTree);

            Console.WriteLine($"Found {_entries.Count(e => e.EntryType != VolumeEntryType.Directory)} files to pack.");
        }

        /// <summary>
        /// Builds the volume.
        /// </summary>
        /// <param name="outputFile"></param>
        public void Build(string outputFile, uint baseRealTocOffset)
        {
            Console.WriteLine("Building volume.");
            BaseRealTocOffset = baseRealTocOffset;
            using var fs = new FileStream(outputFile, FileMode.Create);
            using var volStream = new BinaryStream(fs);

            // Write fake 2.2 TOC. Polyphony wrote a fake toc to make people think GT3 tools worked on it.
            // The start offset is constant and writen into the executables as a page offset.
            volStream.WriteInt32(TocHeader.MagicValueEncrypted, ByteConverter.Big);

            // 2.2
            volStream.WriteInt16(2); // Version Minor
            volStream.WriteInt16(2); // Version Major

            volStream.BaseStream.Seek(BaseRealTocOffset, SeekOrigin.Begin);

            BuildRealTOC(volStream);

            Console.WriteLine($"Done, folder packed to {Path.GetFullPath(outputFile)}.");
        }

        public void SetEncrypted(bool encrypted)
           => Encrypt = encrypted;

        public void SetCompressed(bool compress)
           => Compress = compress;

        public void SetBlockSize(ushort blockSize)
            => BlockSize = blockSize;

        /// <summary>
        /// Builds the real table of contents.
        /// </summary>
        /// <param name="volStream"></param>
        private void BuildRealTOC(BinaryStream volStream)
        {
            if (Encrypt)
                volStream.WriteInt32(TocHeader.MagicValueEncrypted, ByteConverter.Big);
            else
                volStream.WriteString(TocHeader.Magic, StringCoding.Raw);

            volStream.WriteInt32(TocHeader.Version3_1);

            // Skip the block toc as it can't be written for now
            volStream.BaseStream.Position = BaseRealTocOffset + TocHeader.HeaderSize;

            try
            {
                // Start writing the files.
                WriteFiles();

                // Write all the file & directory entries
                WriteBlocks();

                // We've got enough to build the header and merge blocks together now
                BuildTocHeader(volStream);

                // Merge toc and file blob.
                using var fs = new FileStream("gtfiles.temp", FileMode.Open);
                Console.WriteLine($"Merging Data and ToC... ({Utils.BytesToString(fs.Length)})");

                int count = 0;
                byte[] buffer = new byte[32_768];
                while ((count = fs.Read(buffer, 0, buffer.Length)) > 0)
                    volStream.BaseStream.Write(buffer, 0, count);
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

        private void BuildTocHeader(BinaryStream volStream)
        {
            volStream.Position = BaseRealTocOffset + TocHeader.HeaderSize;
            int blockCount = _blocks.Count;

            // The game will subtract the current and next offset to 
            // determine the length of a block, thus the extra one will be the boundary
            volStream.Position += 4 * blockCount;
            volStream.Position += 4;

            // Actually write the toc blocks.
            for (int i = 0; i < blockCount; i++)
            {
                int blockOffset = (int)(volStream.Position - BaseRealTocOffset);

                byte[] block = _blocks[i].Buffer;
                byte[] copy = new byte[block.Length];
                Span<byte> copySpan = copy.AsSpan(0, block.Length);
                block.AsSpan().CopyTo(copySpan);

                if (Encrypt)
                    Utils.XorEncryptFast(copySpan, 0x55);
                var blockComp = PS2Zip.ZlibCodecCompress(copy);

                volStream.WriteBytes(blockComp);

                using (volStream.TemporarySeek(BaseRealTocOffset + TocHeader.HeaderSize + (i * 4), SeekOrigin.Begin))
                    volStream.WriteInt32(Encrypt ? EncryptOffset(blockOffset, i) : blockOffset);
                    
            }

            int tocLength = (int)(volStream.Position - BaseRealTocOffset);
            volStream.Align(BlockSize, grow: true);

            // Write the final offset
            _baseDataOffset = (int)(volStream.Position - BaseRealTocOffset);
            using (volStream.TemporarySeek(BaseRealTocOffset + TocHeader.HeaderSize + (blockCount * 4), SeekOrigin.Begin))
                volStream.WriteInt32(Encrypt ? EncryptOffset(tocLength, blockCount) : tocLength);

            // Finish up actual header
            using (volStream.TemporarySeek(BaseRealTocOffset + 8, SeekOrigin.Begin))
            {
                int pageCount = _baseDataOffset / BlockSize;
                volStream.WriteInt32(tocLength);
                volStream.WriteInt32(pageCount);
                volStream.WriteUInt16((ushort)BlockSize);
                volStream.WriteUInt16((ushort)blockCount);
            }
        }

        private void WriteFiles()
        {
            using var fs = new FileStream("gtfiles.temp", FileMode.Create);
            using var bs = new BinaryStream(fs);

            int i = 1;
            int count = _entries.Count(c => c.EntryType != VolumeEntryType.Directory);

            WriteDirectory(bs, RootTree, "", ref i, ref count);
        }

        private void WriteDirectory(BinaryStream fileWriter, DirEntry parentDir, string path, ref int currentIndex, ref int count)
        {
            foreach (var entry in parentDir.ChildEntries)
            {
                if (entry.EntryType == VolumeEntryType.Directory)
                {
                    string subPath = string.IsNullOrEmpty(path) ? entry.Name : $"{path}/{entry.Name}";
                    WriteDirectory(fileWriter, (DirEntry)entry, subPath, ref currentIndex, ref count);
                }
                else
                {
                    string filePath = string.IsNullOrEmpty(path) ? entry.Name : $"{path}/{entry.Name}";

                    int entrySize = 0;
                    if (entry.EntryType == VolumeEntryType.File)
                    {
                        entrySize = ((FileEntry)entry).Size;
                        ((FileEntry)entry).PageOffset = (int)Math.Round((double)(fileWriter.Position / BlockSize), MidpointRounding.AwayFromZero);
                    }
                    else if (entry.EntryType == VolumeEntryType.CompressedFile)
                    {
                        entrySize = ((CompressedFileEntry)entry).Size;
                        ((CompressedFileEntry)entry).PageOffset = (int)Math.Round((double)(fileWriter.Position / BlockSize), MidpointRounding.AwayFromZero);
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

                    fileWriter.Align(BlockSize, grow: true);
                    currentIndex++;
                }
            }
        }

        private static int EncryptOffset(int offset, int index)
            => offset ^ index * Volume.OffsetCryptKey + Volume.OffsetCryptKey;

        /// <summary>
        /// Serializes all the entries into toc data blocks.
        /// </summary>
        /// <returns></returns>
        private void WriteBlocks()
        {
            CurrentIndexBlock = new IndexBlock(BlockSize);
            CurrentEntryBlock = new EntryBlock(BlockSize);

            // Used to keep track of where the last index block is so we add it 
            // before the new entry blocks, every time
            int lastIndexBlockPos = 0;

            Entry entry;
            while (_currentEntry < _entries.Count)
            {
                entry = _entries[_currentEntry];

                // Write to the next index block
                if (CurrentEntryBlock.HasSpaceToWriteEntry(entry))
                    CurrentEntryBlock.WriteEntry(entry);
                else
                {
                    CurrentEntryBlock.FinalizeHeader();
                    CurrentEntryBlock.LastEntry = _entries[_currentEntry - 1];

                    // Entry block has ran out of space - insert it in the index block if we can
                    if (CurrentIndexBlock.HasSpaceToWriteEntry(_entries[_currentEntry - 1], entry))
                        CurrentIndexBlock.WriteNextDataEntry(_entries[_currentEntry - 1], entry);
                    else
                    {
                        // Dirty hack - we don't need the last one
                        CurrentIndexBlock.EntryCount--;

                        // Index also ran out of space? Well new one
                        CurrentIndexBlock.FinalizeHeader();

                        // Keep track of the cutoff
                        CurrentIndexBlock.PrevBlockLastEntry = _entries[_currentEntry - 1];
                        CurrentIndexBlock.NextBlockFirstEntry = entry;

                        _blocks.Insert(lastIndexBlockPos, CurrentIndexBlock);
                        _indexBlocks.Add(CurrentIndexBlock);

                        var newIndexBlock = new IndexBlock(BlockSize);
                        newIndexBlock.PreviousBlock = CurrentIndexBlock;
                        CurrentIndexBlock.NextBlock = newIndexBlock;
                        CurrentIndexBlock = newIndexBlock;
                        lastIndexBlockPos = _blocks.Count;

                        CurrentIndexBlock.WriteNextDataEntry(_entries[_currentEntry - 1], entry);
                    }

                    _blocks.Add(CurrentEntryBlock);
                    var newEntryBlock = new EntryBlock(BlockSize);
                    newEntryBlock.PreviousBlock = CurrentEntryBlock;
                    CurrentEntryBlock.NextBlock = newEntryBlock;
                    CurrentEntryBlock = newEntryBlock;
                    CurrentEntryBlock.WriteEntry(entry);
                }

                if (_currentEntry == _entries.Count - 1)
                {
                    CurrentEntryBlock.LastEntry = _entries[_currentEntry - 1];

                    if (CurrentIndexBlock.EntryCount == 0)
                    {
                        _blocks.Remove(CurrentIndexBlock);
                        _indexBlocks.Remove(CurrentIndexBlock);
                        CurrentIndexBlock = null;
                    }
                    else if (!_blocks.Contains(CurrentIndexBlock))
                    {
                        _blocks.Insert(lastIndexBlockPos, CurrentIndexBlock);
                        _indexBlocks.Add(CurrentIndexBlock);
                    }

                    // If needed
                    CurrentEntryBlock.FinalizeHeader();
                    CurrentIndexBlock.FinalizeHeader();
                }

                _currentEntry++;
            }

            // If nothing was writen in the new blocks, just discard them
            FinalizeBlocks();

            // Is there more than one index block? If so we may need to write a master one
            CreateMainIndexBlockIfNeeded();

            // Link all the relational blocks together
            AssignBlockLinks();

            // Point index blocks to their child block
            if (_mainIndexBlock != null)
            {
                SpanWriter sw = new SpanWriter(_mainIndexBlock.Buffer);

                // This will include the terminator
                for (int i = 0; i < _indexBlocks.Count; i++)
                {
                    var indexBlock = _indexBlocks[i];
                    sw.Position = BlockSize - ((i + 1) * 0x08);
                    sw.Position += 4;
                    sw.WriteInt32(indexBlock.BlockIndex);
                }
            }

            for (int i = 0; i < _indexBlocks.Count; i++)
            {
                var indexBlock = _indexBlocks[i];
                int pageIndex = indexBlock.BlockIndex;

                SpanWriter sw = new SpanWriter(indexBlock.Buffer);
                for (int j = 0; j < indexBlock.EntryCount; j++)
                {
                    sw.Position = BlockSize - ((j + 1) * 0x08);
                    sw.Position += 4;
                    sw.WriteInt32(pageIndex + 1 + j);
                }

                // Write last block terminator
                sw.Position = BlockSize - ((indexBlock.EntryCount + 1) * 0x08);
                sw.Position += 4;
                sw.WriteInt32(pageIndex + indexBlock.EntryCount + 1);
            }
        }

        private void FinalizeBlocks()
        {
            // If nothing was writen in the last blocks, just null them
            if (CurrentEntryBlock.EntryCount == 0)
            {
                _blocks.Remove(CurrentEntryBlock);
                CurrentEntryBlock = null;
            }
            else
            {
                if (!_blocks.Contains(CurrentEntryBlock))
                    _blocks.Add(CurrentEntryBlock);
            }
        }

        private void AssignBlockIndexes()
        {
            for (int i = 0; i < _blocks.Count; i++)
                _blocks[i].BlockIndex = i;
        }

        private void CreateMainIndexBlockIfNeeded()
        {
            if (_indexBlocks.Count > 1)
            {
                _mainIndexBlock = new IndexBlock(BlockSize);
                _mainIndexBlock.IsMasterBlock = true;
                _blocks.Insert(0, _mainIndexBlock);

                AssignBlockIndexes();

                // We only need the middles.
                for (int i = 0; i < _indexBlocks.Count; i++)
                {                    
                    IndexBlock indexBlock = _indexBlocks[i];
                    var nextIndexBlock = indexBlock.NextBlock as IndexBlock;
                    if (nextIndexBlock is null) 
                        break;

                    Entry lastPrev = (_blocks[nextIndexBlock.BlockIndex - 1] as EntryBlock).LastEntry;

                    if (indexBlock.BlockIndex + 1 >= _blocks.Count)
                        break;

                    Entry firstNext = (_blocks[nextIndexBlock.BlockIndex + 1] as EntryBlock).FirstEntry;
                    _mainIndexBlock.WriteNextDataEntry(lastPrev, firstNext);
                }

                _mainIndexBlock.FinalizeHeader();
            }
            else
                AssignBlockIndexes();
        }

        private void AssignBlockLinks()
        {
            for (int i = 0; i < _blocks.Count; i++)
            {
                var block = _blocks[i];
                if (block is IndexBlock indexBlock && indexBlock.IsMasterBlock)
                {
                    indexBlock.WriteNextPage(-1);
                    indexBlock.WritePreviousPage(-1);
                }
                else
                {
                    block.WriteNextPage(block.NextBlock?.BlockIndex ?? -1);
                    block.WritePreviousPage(block.PreviousBlock?.BlockIndex ?? -1);
                }
            }
        }

        /// <summary>
        /// Imports a local file directory as a game directory node.
        /// </summary>
        /// <param name="parent"></param>
        /// <param name="folder"></param>
        private void Import(DirEntry parent, string folder)
        {
            var dirEntries = Directory.EnumerateFileSystemEntries(folder)
                .OrderBy(e => e, StringComparer.Ordinal).ToList();

            foreach (var path in dirEntries)
            {
                Entry entry;

                string relativePath = path.Substring(folder.Length + 1);
                if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
                {
                    entry = new DirEntry(relativePath);
                    entry.NodeID = CurrentID++;
                    Import((DirEntry)entry, path);
                }
                else
                {                    
                    string absolutePath = Path.Combine(folder, relativePath);
                    string volumePath = absolutePath.Substring(InputFolder.Length + 1);

                    var fInfo = new FileInfo(absolutePath);
                    if (Compress && IsNormallyCompressedVolumeFile(volumePath))
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
                    entry.NodeID = CurrentID++;
                }

                entry.ParentNode = parent.NodeID;
                parent.ChildEntries.Add(entry);
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
        private void TraverseBuildEntryPackList(DirEntry parentDir)
        {
            foreach (var entry in parentDir.ChildEntries)
                _entries.Add(entry);

            foreach (var entry in parentDir.ChildEntries)
            {
                if (entry is DirEntry childDir)
                    TraverseBuildEntryPackList(childDir);
            }
        }

        public static uint GetRealToCOffsetForGame(GameVolumeType game)
        {
            return game switch
            {
                GameVolumeType.GTHD => 0x1 * Volume.DefaultBlockSize,

                // From this point on, 17mb+ of wasted space..
                GameVolumeType.TT => 0x2231 * Volume.DefaultBlockSize,
                GameVolumeType.TT_DEMO => 0x2159 * Volume.DefaultBlockSize,
                GameVolumeType.GT4 => 0x2159 * Volume.DefaultBlockSize,
                GameVolumeType.GT4_MX5_DEMO => 0x2159 * Volume.DefaultBlockSize,
                GameVolumeType.GT4_FIRST_PREV => 0x2159 * Volume.DefaultBlockSize,
                GameVolumeType.GT4_ONLINE => 0x22B7 * Volume.DefaultBlockSize,
                _ => 0x800,
            };
        }
    }
}
