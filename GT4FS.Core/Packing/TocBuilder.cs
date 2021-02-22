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

namespace GT4FS.Core.Packing
{
    public class TocBuilder
    {
        public const int BlockHeaderSize = 0x0C;

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

        // For the indexing blocks
        private List<Entry> _firstBlockEntries = new List<Entry>();

        // List of all TOC block buffers, kept in memory to write the previous/next pages and page offsets later on.
        public List<byte[]> _entryBlocks;
        public List<byte[]> _indexBlocks;

        /// <summary>
        /// Current node ID.
        /// </summary>
        public int CurrentID = 1;

        public void RegisterFilesToPack(string mainFolder, bool useCache)
        {
            Console.WriteLine("[*] Indexing folder to prepare to pack..");

            RootTree = new DirEntry(".");
            RootTree.NodeID = CurrentID++;

            _entries.Add(RootTree);

            Import(RootTree, mainFolder);
            TraverseBuildEntryPackList(RootTree);
            Console.WriteLine("[*] Done.");
        }

        /// <summary>
        /// Builds the volume.
        /// </summary>
        /// <param name="outputFile"></param>
        public void Build(string outputFile, int baseRealTocOffset)
        {
            using (var fs = new FileStream(outputFile, FileMode.Create))
            using (var volStream = new BinaryStream(fs))
            {
                // Write fake TOC
                volStream.BaseStream.Seek(baseRealTocOffset);

                BuildRealTOC(volStream, baseRealTocOffset);
            }
        }

        /// <summary>
        /// Builds the real table of contents.
        /// </summary>
        /// <param name="volStream"></param>
        private void BuildRealTOC(BinaryStream volStream, int baseRealTocOffset)
        {
            volStream.WriteInt32(TocHeader.MagicValue);
            volStream.WriteInt32(TocHeader.Version3_1);

            // Skip the block toc as it can't be written for now
            volStream.BaseStream.Position = baseRealTocOffset + TocHeader.HeaderSize;

            // Write all the data blocks.
            WriteEntryMetaBlocks();

            // Write Index blocks based on the data blocks.
            _currentEntry = 0;
            WriteIndexBlocks();

            // Fill up index block's next/previous now that they've been correctly made
            FillIndexBlockLinks();

            // We got the index block count, so we can write the next/previous for our entries too
            FillEntryMetaBlockLinks();

            // We've written all of our blocks, and we now know their order - proceed to fill in the index blocks with the entry's block indexes
            FillIndexBlockEntryIndexes();

            // We've got enough to build the header and merge blocks together now
            BuildTocHeader(volStream, baseRealTocOffset);

            // At that point, we can start writing the files.
            // TODO
        }

        private void BuildTocHeader(BinaryStream volStream, int baseRealTocOffset)
        {
            volStream.Position = baseRealTocOffset + TocHeader.HeaderSize;
            volStream.Position += 4; // Skip the first one

            int blockCount = _indexBlocks.Count + _entryBlocks.Count;
            List<byte[]> mergedBlocks = new List<byte[]>(blockCount);
            mergedBlocks.AddRange(_indexBlocks);
            mergedBlocks.AddRange(_entryBlocks);

            volStream.Position += 4 * mergedBlocks.Count;
            volStream.Position += 4;

            int entryBlocksOffset = (int)volStream.Position;
            using (volStream.TemporarySeek(baseRealTocOffset + TocHeader.HeaderSize, SeekOrigin.Begin))
                volStream.WriteInt32(entryBlocksOffset ^ 0 * Volume.OffsetCryptKey + Volume.OffsetCryptKey);

            // Actually write the tocs.
            for (int i = 0; i < mergedBlocks.Count; i++)
            {
                int blockOffset = (int)volStream.Position - baseRealTocOffset;

                byte[] block = mergedBlocks[i];
                byte[] copy = new byte[block.Length];
                Span<byte> copySpan = copy.AsSpan(0, block.Length);
                block.AsSpan().CopyTo(copySpan);

                Utils.XorEncryptFast(copySpan, 0x55);
                var blockComp = PS2Zip.ZlibCodecCompress(copy);

                volStream.WriteBytes(blockComp);

                using (volStream.TemporarySeek(baseRealTocOffset + TocHeader.HeaderSize + ((i + 1) * 4), SeekOrigin.Begin))
                    volStream.WriteInt32(EncryptOffset(blockOffset, i + 1));
            }

            // Write the final offset
            int dataOffset = (int)volStream.Position;
            using (volStream.TemporarySeek(baseRealTocOffset + TocHeader.HeaderSize + ((mergedBlocks.Count + 1) * 4), SeekOrigin.Begin))
                volStream.WriteInt32(EncryptOffset(dataOffset, mergedBlocks.Count + 1));

            // Finish up actual header
            using (volStream.TemporarySeek(baseRealTocOffset + 8, SeekOrigin.Begin))
            {
                int pageCount = (int)Math.Round((double)(dataOffset / BlockSize), MidpointRounding.AwayFromZero);
                volStream.WriteInt32(pageCount);
                volStream.WriteInt32(dataOffset);
                volStream.WriteUInt16((ushort)BlockSize);
                volStream.WriteUInt16((ushort)(mergedBlocks.Count + 1));
            }



        }

        private static int EncryptOffset(int offset, int index)
            => offset ^ index * Volume.OffsetCryptKey + Volume.OffsetCryptKey;

        public void WriteIndexBlocks()
        {
            _indexBlocks = new List<byte[]>();
            while (_currentEntry < _firstBlockEntries.Count)
            {
                byte[] dataBlock = WriteNextIndexBlock();
                _indexBlocks.Add(dataBlock);
            }
        }

        private byte[] WriteNextIndexBlock()
        {
            byte[] buffer = new byte[BlockSize];
            SpanWriter blockWriter = new SpanWriter(buffer);

            // Skip block header for now
            blockWriter.Position = BlockHeaderSize;
            ushort entriesWriten = 0;

            int blockSpaceLeft = BlockSize - BlockHeaderSize + 4; // Account for the int at the begining of the bottom toc

            while (true)
            {
                if (_currentEntry >= _firstBlockEntries.Count)
                    break; // We are completely done writing the TOC, finish up the block

                Entry entry = _firstBlockEntries[_currentEntry];
                int entrySize = 4 + Encoding.UTF8.GetByteCount(entry.Name);
                if (entrySize > blockSpaceLeft)
                    break; // Predicted exceeded block bound, finish it up

                // Begin to write the entry's common information
                int entryOffset = blockWriter.Position;
                blockWriter.Endian = Endian.Big;
                blockWriter.WriteInt32(entry.ParentNode);
                blockWriter.Endian = Endian.Little;
                blockWriter.WriteString0(entry.Name);
                blockWriter.Align(0x04); // String is aligned

                int endPos = blockWriter.Position;

                blockSpaceLeft -= entrySize + 0x08; // Include the block's toc entry

                // Write the lookup information at the end of the block
                blockWriter.Position = BlockSize - ((entriesWriten + 1) * 0x08);
                blockWriter.WriteUInt16((ushort)entryOffset);
                blockWriter.WriteUInt16((ushort)(4 + Encoding.UTF8.GetByteCount(entry.Name)));
                blockWriter.WriteUInt32(0); // We will write the block index later as we don't have it

                // Move on to next.
                entriesWriten++;
                _currentEntry++;

                blockWriter.Position = endPos;
            }

            // Write up the block info - write what we can write - the entry count
            blockWriter.Position = 0;
            blockWriter.WriteInt16(1); // Block Type - Index
            blockWriter.WriteUInt16((ushort)((entriesWriten * 2) + 1));

            return buffer;
        }

        private void FillIndexBlockLinks()
        {
            for (int i = 0; i < _indexBlocks.Count; i++)
            {
                SpanWriter block = new SpanWriter(_indexBlocks[i]);

                // Next page
                block.Position = 4;
                block.WriteInt32(i != _indexBlocks.Count - 1 ? i + 1 : -1);

                // Previous page
                block.WriteInt32(i != 0 ? i - 1 : -1);

            }
        }

        /// <summary>
        /// Serializes all the entries into toc data blocks.
        /// </summary>
        /// <returns></returns>
        private void WriteEntryMetaBlocks()
        {
            _entryBlocks = new List<byte[]>();
            while (_currentEntry < _entries.Count)
            {
                byte[] dataBlock = WriteNextEntryMetaBlock();
                _entryBlocks.Add(dataBlock);
            }
        }

        private byte[] WriteNextEntryMetaBlock()
        {
            byte[] buffer = new byte[BlockSize];
            SpanWriter blockWriter = new SpanWriter(buffer);

            // Skip block header for now
            blockWriter.Position = BlockHeaderSize;
            ushort entriesWriten = 0;

            int blockSpaceLeft = BlockSize - BlockHeaderSize + 4; // Account for the int at the begining of the bottom toc

            while (true)
            {
                if (_currentEntry >= _entries.Count)
                    break; // We are completely done writing the TOC, finish up the block

                Entry entry = _entries[_currentEntry];
                int entrySize = entry.GetTotalSize(blockWriter.Position);
                if (entrySize > blockSpaceLeft)
                    break; // Predicted exceeded block bound, finish it up

                if (entriesWriten == 0)
                    _firstBlockEntries.Add(entry);

                // Begin to write the entry's common information
                int entryOffset = blockWriter.Position;
                blockWriter.Endian = Endian.Big;
                blockWriter.WriteInt32(entry.ParentNode);
                blockWriter.Endian = Endian.Little;
                blockWriter.WriteString0(entry.Name);
                blockWriter.Align(0x04); // String is aligned

                // Write type specific
                int entryMetaOffset = blockWriter.Position;
                entry.SerializeTypeMeta(ref blockWriter);
                blockWriter.Align(0x04); // Whole entry is also aligned

                int endPos = blockWriter.Position;

                blockSpaceLeft -= entrySize + 0x08; // Include the block's toc entry

                // Write the lookup information at the end of the block
                blockWriter.Position = BlockSize - ((entriesWriten + 1) * 0x08);
                blockWriter.WriteUInt16((ushort)entryOffset);
                blockWriter.WriteUInt16((ushort)entry.GetEntryMetaSize());
                blockWriter.WriteUInt16((ushort)entryMetaOffset);
                blockWriter.WriteUInt16(entry.GetTypeMetaSize());

                // Move on to next.
                entriesWriten++;
                _currentEntry++;

                blockWriter.Position = endPos;
            }

            // Write up the block info - write what we can write - the entry count
            blockWriter.Position = 0;
            blockWriter.WriteInt16(0); // Block Type
            blockWriter.WriteUInt16((ushort)((entriesWriten * 2) + 1));

            return buffer;
        }

        private void FillEntryMetaBlockLinks()
        {
            for (int i = 0; i < _entryBlocks.Count; i++)
            {
                SpanWriter block = new SpanWriter(_entryBlocks[i]);

                // Next page
                block.Position = 4;
                block.WriteInt32(i != _entryBlocks.Count - 1 ? _indexBlocks.Count + (i + 1) : -1);

                // Previous page
                block.WriteInt32(i != 0 ? _indexBlocks.Count + (i - 1) : -1);

            }
        }

        private void FillIndexBlockEntryIndexes()
        {
            int currentEntry = 0;
            for (int i = 0; i < _indexBlocks.Count; i++)
            {
                ushort entryCount = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(_indexBlocks[i].AsSpan(2, 2)) / 2);
                SpanWriter block = new SpanWriter(_indexBlocks[i]);

                for (int j = 0; j < entryCount; j++)
                {
                    block.Position = BlockSize - (0x08 * (j + 1));
                    block.Position += 4;
                    block.WriteInt32((_indexBlocks.Count + 1) + currentEntry++);
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

        private string[] ReadCache(string cacheFile)
        {
            if (!File.Exists(cacheFile))
                return null;
            
            return File.ReadAllLines(cacheFile);
        }

        private void WriteCache(string[] files)
        {
            using (var sw = new StreamWriter("files.txt"))
            {
                foreach (var file in files)
                    sw.WriteLine(file);
            }
        }
    }
}
