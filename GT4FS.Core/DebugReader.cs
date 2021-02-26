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

namespace GT4FS.Core
{
    /// <summary>
    /// Reader based on reversed game code for debugging purposes.
    /// </summary>
    public class DebugReader
    {
        public int TocOffset { get; set; }

        public BinaryStream VolumeStream { get; set; }
        public ushort BlockSize { get; set; } = 0x800;
        public ushort GetBlockSize()
            => BlockSize;

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

            BlockInfo blockInfo = GetBlock(0);
            while (blockInfo.IsIndexBlock)
            {
                Debug.WriteLine($"Searching index block ({blockInfo.BlockIndex}) for {part}");
                int nextBlockIndex = blockInfo.SearchBlockIndex(entryInput, entryInput.Length);
                blockInfo.SwitchToNewIndex(nextBlockIndex);

                if (blockInfo.IsIndexBlock)
                    Debug.WriteLine($"Next index block to search is: {blockInfo.BlockIndex}");
            }

            Debug.WriteLine($"{part} ({parentID}) is located at block index: {blockInfo.BlockIndex}");

            // At that point we have the block of which the entry we're looking for is in
            if (!blockInfo.SearchEntry(entryInput, entryInput.Length, out int resultEntryIndex))
                return null; // return default entry
            else
            {
                Debug.WriteLine($"Found {part} at block {blockInfo.BlockIndex} with entry index {resultEntryIndex}");
                // Read entry and return it using the index
                SpanReader sr = new SpanReader(blockInfo.BlockBuffer);
                sr.Position = BlockSize - (resultEntryIndex * 0x08);
                sr.Position -= 0x04; // Skip to the actual entry's type metadata

                short entryTypeMetaOffset = sr.ReadInt16();
                sr.Position = entryTypeMetaOffset;
                var entry = Entry.ReadEntryFromBuffer(ref sr);
                entry.ParentNode = parentID;
                return entry;
            }
        }

        public BlockInfo GetBlock(int index)
        {
            BlockInfo block = new BlockInfo();
            block.ParentVolume = this;
            block.BlockIndex = -1;
            block.BlockBuffer = GetBlockBuffer(index);
            if (block.BlockBuffer != null)
                block.BlockIndex = index;
            return block;
        }

        public byte[] GetBlockBuffer(int index)
        {
            if (!IsSoFS())
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
                return DecryptBlock(VolumeStream, TocOffset + beginOffset, endOffset - beginOffset);
            }
        }

        public int GetEntryOffset(int blockIndex)
        {
            VolumeStream.Position = TocOffset + TocHeader.HeaderSize + (blockIndex * 4);
            return VolumeStream.ReadInt32();
        }

        public int GetEntryOffsetSecure(int blockIndex)
        {
            VolumeStream.Position = TocOffset + TocHeader.HeaderSize + (blockIndex * 4);
            return VolumeStream.ReadInt32() ^ blockIndex * Volume.OffsetCryptKey + Volume.OffsetCryptKey;
        }

        public bool IsSoFS()
        {
            VolumeStream.Position = TocOffset;
            return VolumeStream.ReadInt32(ByteConverter.Big) == TocHeader.MagicValue;
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
        public byte[] DecryptBlock(BinaryStream bs, long offset, int length)
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
            ushort blockCount = VolumeStream.ReadUInt16();

            using var sw = new StreamWriter("block_info.txt");

            for (int i = 0; i < blockCount; i++)
            {
                BlockInfo block = GetBlock(i);
                SpanReader sr = new SpanReader(block.BlockBuffer);

                short blockType = sr.ReadInt16();
                short entryCount = sr.ReadInt16();
                int realEntryCount = (entryCount / 2);

                sw.WriteLine($"Block #{i} {(blockType == 1 ? "[INDEXER]" : "")} - {entryCount} entries [{realEntryCount} actual]");

                for (int j = 0; j < realEntryCount; j++)
                {
                    sr.Position = BlockSize - (j * 0x08) - 0x08;
                    short entryOffset = sr.ReadInt16();
                    short entryLen = sr.ReadInt16();

                    if (blockType == 1)
                    {
                        int blockIndex = sr.ReadInt16();
                        sr.Position = entryOffset;

                        sr.Endian = Syroot.BinaryData.Core.Endian.Big;
                        int parentNode = sr.ReadInt32();
                        sr.Endian = Syroot.BinaryData.Core.Endian.Little;

                        string str;
                        if (entryLen >= 4)
                            str = sr.ReadStringRaw(entryLen - 4);
                        else
                            str = "string was null";

                        sw.WriteLine($"{j} -> Offset: {entryOffset:X2} - Length: {entryLen} - Points to Block: {blockIndex} | ParentNode: {parentNode}, Name: {str}");
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

                        sw.WriteLine($"{j} -> Offset: {entryOffset:X2} - Length: {entryLen} - Data Offset: {entryMetaTypeOffset} -  Data Len: {entryMetaTypeLen} | | ParentNode: {parentNode}, Name: {str}");
                    }
                }

                sw.WriteLine();
            }
        }

        public void Close()
            => VolumeStream?.Dispose();
    }

    public class BlockInfo
    {
        public DebugReader ParentVolume { get; set; }
        public int BlockIndex { get; set; }
        public byte[] BlockBuffer { get; set; }

        public bool IsIndexBlock 
            => BinaryPrimitives.ReadInt16LittleEndian(BlockBuffer) == 1;

        public void SwitchToNewIndex(int index)
        {
            if (BlockIndex != -1)
                BlockIndex = -1;

            BlockBuffer = ParentVolume.GetBlockBuffer(index);
            if (BlockBuffer != null)
                BlockIndex = index;
        }

        public int SearchBlockIndex(byte[] input, int inputLen)
        {
            SpanReader sr = new SpanReader(BlockBuffer);
            sr.Position = 2;

            int indexEntryCount = sr.ReadInt16();
            int realEntryCount = (((indexEntryCount - 1) / 2) - 1);
            ushort blockSize = ParentVolume.GetBlockSize();

            if (indexEntryCount == 1)
            {
                // return the first
                sr.Position = blockSize - 4;
                return sr.ReadInt32();
            }

            // First
            sr.Position = blockSize - 0x08;
            short entryOffset = sr.ReadInt16();
            short entryLength = sr.ReadInt16();
            sr.Position = entryOffset;
            byte[] entryIndexer = sr.ReadBytes(entryLength);
            int diff = DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength);

            if (diff < 0)
            {
                // return the first block index
                sr.Position = (blockSize - 0x08) + 4;
                return sr.ReadInt32();
            }

            // Last
            sr.Position = blockSize - (realEntryCount * 0x08);
            entryOffset = sr.ReadInt16();
            entryLength = sr.ReadInt16();
            sr.Position = entryOffset;
            entryIndexer = sr.ReadBytes(entryLength);
            diff = DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength);

            if (diff > 0)
            {
                // return the last block index
                sr.Position = blockSize - (realEntryCount * 0x08) + 4;
                return sr.ReadInt32();
            }

            int min = 0;
            int max = realEntryCount;
            int mid = (min + max) / 2;

            while (min < max)
            {
                if (max - min < 8)
                {
                    mid = min;
                    int baseOffset = blockSize - (mid * 0x08);
                    int entryitorOffset = baseOffset;
                    if (max < min)
                        return -1;

                    while (min <= max)
                    {
                        entryitorOffset -= 0x08;
                        sr.Position = entryitorOffset;
                        entryOffset = sr.ReadInt16();

                        int entryIndexOffset = (mid * 0x08 - blockSize) + baseOffset + entryitorOffset;
                        sr.Position = entryIndexOffset + 2;

                        entryLength = sr.ReadInt16();
                        sr.Position = entryOffset;
                        entryIndexer = sr.ReadBytes(entryLength);
                        diff = DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength);
                        if (diff == 0)
                        {
                            sr.Position = entryIndexOffset - 4;
                            return sr.ReadInt32();
                        }
                        else if (diff < 0)
                        {
                            sr.Position = entryIndexOffset + 4;
                            return sr.ReadInt32();
                        }

                        min++;
                    }

                    return -1;
                }

                sr.Position = blockSize - (mid * 0x08);
                entryOffset = sr.ReadInt16();
                entryLength = sr.ReadInt16();
                sr.Position = entryOffset;
                entryIndexer = sr.ReadBytes(entryLength);

                diff = DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength);
                if (diff == 0)
                {
                    // return the matching index
                    sr.Position = blockSize - (mid * 0x08) + 4;
                    return sr.ReadInt32();
                }
                else if (diff > 0)
                    min = mid;
                else
                    max = mid;

                mid = (min + max) / 2;
            }

            throw new Exception("Failed to bsearch for the block entry.");
        }

        public bool SearchEntry(byte[] input, int inputLen, out int result)
        {
            result = 0;

            SpanReader sr = new SpanReader(BlockBuffer);
            sr.Position = 2;

            int entryCount = sr.ReadInt16() / 2;
            ushort blockSize = ParentVolume.GetBlockSize();

            // Check first
            sr.Position = blockSize - 0x08;
            short entryOffset = sr.ReadInt16();
            short entryLength = sr.ReadInt16();
            sr.Position = entryOffset;
            byte[] entryIndexer = sr.ReadBytes(entryLength);

            int diff = DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength);
            if (diff < 0)
            {
                result = 0;
                return false;
            }

            // Check last
            sr.Position = blockSize - (entryCount * 0x08);
            entryOffset = sr.ReadInt16();
            entryLength = sr.ReadInt16();
            sr.Position = entryOffset;
            entryIndexer = sr.ReadBytes(entryLength);
            diff = DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength);
            if (diff > 0)
            {
                result = -entryCount;
                return false;
            }

            int min = 0;
            int max = entryCount;
            int mid = (min + max) / 2;

            while (min < max)
            {
                if (max - min < 8)
                {
                    mid = min;
                    int baseOffset = blockSize - (mid * 0x08);
                    int entryitorOffset = baseOffset;
                    if (max < min)
                        return false;

                    while (min <= max)
                    {
                        entryitorOffset -= 0x08;
                        sr.Position = entryitorOffset;
                        entryOffset = sr.ReadInt16();

                        sr.Position = (mid * 0x08 - blockSize) + baseOffset + entryitorOffset;
                        sr.Position += 2;

                        entryLength = sr.ReadInt16();
                        sr.Position = entryOffset;
                        entryIndexer = sr.ReadBytes(entryLength);
                        diff = DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength);
                        if (diff < 1)
                        {
                            result = min;
                            return diff == 0;
                        }

                        min++;
                    }

                    return false;
                }

                sr.Position = blockSize - (mid * 0x08);
                entryOffset = sr.ReadInt16();
                entryLength = sr.ReadInt16();
                sr.Position = entryOffset;
                entryIndexer = sr.ReadBytes(entryLength);

                diff = DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength);
                if (diff == 0)
                {
                    result = mid - 1;
                    return true;
                }
                else if (diff > 0)
                    min = mid;
                else
                    max = mid;

                mid = (min + max) / 2;
            }

            throw new Exception("Failed to bsearch the entry.");
        }
    }
}
