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
using GT4FS.Core;
using GT4FS.Core.Entries;

namespace GT4FS.Core.Packing
{
    public class TocBuilder
    {
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
            Console.WriteLine("Indexing folder to prepare to pack..");
            InputFolder = inputFolder;

            RootTree = new DirEntry(".");
            RootTree.NodeID = CurrentID++;

            _entries.Add(RootTree);

            Import(RootTree, InputFolder);
            TraverseBuildEntryPackList(RootTree);
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

            // Write fake TOC
            volStream.WriteInt32(TocHeader.MagicValue, ByteConverter.Big);
            volStream.WriteInt16(2);
            volStream.WriteInt16(2);

            volStream.BaseStream.Seek(BaseRealTocOffset, SeekOrigin.Begin);

            BuildRealTOC(volStream);
        }

        /// <summary>
        /// Builds the real table of contents.
        /// </summary>
        /// <param name="volStream"></param>
        private void BuildRealTOC(BinaryStream volStream)
        {
            volStream.WriteInt32(TocHeader.MagicValue, ByteConverter.Big);
            volStream.WriteInt32(TocHeader.Version3_1);

            // Skip the block toc as it can't be written for now
            volStream.BaseStream.Position = BaseRealTocOffset + TocHeader.HeaderSize;

            // Start writing the files.
            WriteFiles();

            // Write all the entries.
            WriteBlocks();

            // We've got enough to build the header and merge blocks together now
            BuildTocHeader(volStream);

            
            Console.WriteLine("[*] Merging data and toc...");
            // Merge toc and file blob.
            using var fs = new FileStream("gtfiles.temp", FileMode.Open);
            int count = 0;
            byte[] buffer = new byte[32_768];
            while ( (count = fs.Read(buffer, 0, buffer.Length)) > 0)
                volStream.BaseStream.Write(buffer, 0, count);
            

            Console.WriteLine("Done.");
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

                Utils.XorEncryptFast(copySpan, 0x55);
                var blockComp = PS2Zip.ZlibCodecCompress(copy);

                volStream.WriteBytes(blockComp);

                using (volStream.TemporarySeek(BaseRealTocOffset + TocHeader.HeaderSize + (i * 4), SeekOrigin.Begin))
                    volStream.WriteInt32(EncryptOffset(blockOffset, i));
            }

            int tocLength = (int)(volStream.Position - BaseRealTocOffset);
            volStream.Align(BlockSize, grow: true);

            // Write the final offset
            _baseDataOffset = (int)(volStream.Position - BaseRealTocOffset);
            using (volStream.TemporarySeek(BaseRealTocOffset + TocHeader.HeaderSize + (blockCount * 4), SeekOrigin.Begin))
                volStream.WriteInt32(EncryptOffset(tocLength, blockCount));

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

            WriteDirectory(bs, RootTree, "");
        }

        private void WriteDirectory(BinaryStream fileWriter, DirEntry parentDir, string path)
        {
            foreach (var entry in parentDir.ChildEntries)
            {
                if (entry.EntryType == VolumeEntryType.Directory)
                {
                    string subPath = string.IsNullOrEmpty(path) ? entry.Name : $"{path}/{entry.Name}";
                    WriteDirectory(fileWriter, (DirEntry)entry, subPath);
                }
                else
                {
                    string filePath = string.IsNullOrEmpty(path) ? entry.Name : $"{path}/{entry.Name}";
                    Console.WriteLine($"Writing: {filePath}");

                    ((FileEntry)entry).PageOffset = (int)Math.Round((double)(fileWriter.Position / BlockSize), MidpointRounding.AwayFromZero);

                    var file = File.ReadAllBytes(Path.Combine(InputFolder, filePath));
                    
                    //var fileComp = PS2Zip.ZlibCodecCompress(file);
                    fileWriter.Write(file);
                    fileWriter.Align(BlockSize, grow: true);
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
                    entry = new FileEntry(relativePath);
                    string absolutePath = Path.Combine(folder, relativePath);
                    var fInfo = new FileInfo(absolutePath);

                    ((FileEntry)entry).Size = (int)fInfo.Length;
                    ((FileEntry)entry).ModifiedDate = fInfo.LastWriteTimeUtc;
                    entry.NodeID = CurrentID++;
                }

                entry.ParentNode = parent.NodeID;
                parent.ChildEntries.Add(entry);
            }
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
    }
}
