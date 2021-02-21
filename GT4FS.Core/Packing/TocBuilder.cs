using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.IO;
using Syroot.BinaryData;
using Syroot.BinaryData.Core;
using Syroot.BinaryData.Memory;

namespace GT4FS.Core.Packing
{
    public class TocBuilder
    {
        public const int BlockHeaderSize = 0x08;

        /// <summary>
        /// Size of each block. Defaults to 0x800.
        /// </summary>
        public int BlockSize { get; set; } = Volume.DefaultBlockSize;

        /// <summary>
        /// The root of the folder structure - used to build the relationship between files.
        /// </summary>
        public DirEntry RootTree { get; set; }

        // In-one-go entries of the whole file system for writing later on.
        private List<Entry> _entries;

        // To keep track of which entry is currently being saved
        private int _currentEntry = 0;

        // For the indexing blocks
        private List<Entry> _firstBlockEntries = new List<Entry>();

        // List of all TOC block buffers, kept in memory to write the previous/next pages and page offsets later on.
        public List<byte[]> _entryBlocks;

        /// <summary>
        /// Current node ID.
        /// </summary>
        public int CurrentID = 1;

        public void RegisterFilesToPack(string mainFolder, bool useCache)
        {
            Console.WriteLine("[*] Indexing folder to prepare to pack..");
            Import(RootTree, mainFolder);
            Console.WriteLine("[*] Done.");
        }

        /// <summary>
        /// Builds the volume.
        /// </summary>
        /// <param name="outputFile"></param>
        public void Build(string outputFile)
        {
            using (var fs = new FileStream(outputFile, FileMode.Create))
            using (var volStream = new BinaryStream(fs))
            {
                // Write fake TOC
                volStream.BaseStream.Seek(0x800);

                BuildRealTOC(volStream);
            }
        }

        /// <summary>
        /// Builds the real table of contents.
        /// </summary>
        /// <param name="volStream"></param>
        private void BuildRealTOC(BinaryStream volStream)
        {
            volStream.WriteInt32(TocHeader.MagicValue);
            volStream.WriteInt32(TocHeader.Version3_1);

            // Skip the block toc as it can't be written for now
            volStream.BaseStream.Position = TocHeader.HeaderSize;

            // Write all the data blocks.
            WriteDataBlocks();

            // TODO: Write Index blocks based on the data blocks.
        }

        private byte[] WriteNextBlock()
        {
            byte[] buffer = new byte[BlockSize];
            SpanWriter blockWriter = new SpanWriter(buffer);

            // Skip block header for now
            blockWriter.Position = BlockHeaderSize;
            ushort entriesWriten = 0;

            int blockSpaceLeft = BlockSize - BlockHeaderSize;

            while (true)
            {
                if (_currentEntry >= _entries.Count)
                    break; // We are completely done writing the TOC, finish up the block

                Entry entry = _entries[_currentEntry];
                int entrySize = entry.GetTotalSize(blockWriter.Position);
                if (entrySize < blockSpaceLeft)
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

                int endPos = blockWriter.Position;

                blockSpaceLeft -= entrySize + 0x08; // Include the block's toc entry

                // Write the lookup information at the end of the block
                blockWriter.Position = BlockSize - (entriesWriten * 0x08);
                blockWriter.WriteUInt16((ushort)entryOffset);
                blockWriter.WriteUInt16((ushort)(entry.Name.Length + 4));
                blockWriter.WriteUInt16((ushort)entryMetaOffset);
                blockWriter.WriteUInt16(entry.GetMetaSize());
                blockWriter.Align(0x04); // Whole entry is also aligned


                // Move on to next.
                entriesWriten++;
                blockWriter.Position = endPos;
            }

            // Write up the block info - write what we can write - the entry count
            blockWriter.Position = 0x04;
            blockWriter.WriteUInt16(entriesWriten);

            return buffer;
        }

        /// <summary>
        /// Serializes all the entries into toc data blocks.
        /// </summary>
        /// <returns></returns>
        private void WriteDataBlocks()
        {
            _entryBlocks = new List<byte[]>();
            while (_currentEntry < _entries.Count)
            {
                byte[] dataBlock = WriteNextBlock();
                _entryBlocks.Add(dataBlock);
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

            _entries = TraverseBuildEntryPackList(RootTree);
        }

        /// <summary>
        /// Builds the 2D representation of the file system, for packing.
        /// </summary>
        /// <param name="parentDir"></param>
        private List<Entry> TraverseBuildEntryPackList(DirEntry parentDir)
        {
            var entries = new List<Entry>();
            foreach (var entry in RootTree.ChildEntries)
                entries.Add(entry);

            foreach (var entry in RootTree.ChildEntries)
            {
                if (entry is DirEntry childDir)
                    TraverseBuildEntryPackList(childDir);
            }

            return entries;
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
