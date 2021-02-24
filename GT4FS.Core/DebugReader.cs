using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

using Syroot.BinaryData;
using Syroot.BinaryData.Memory;

using GT4FS.Core.Packing;

namespace GT4FS.Core
{
    /// <summary>
    /// Reader based on reversed game code for debugging purposes.
    /// </summary>
    public class DebugReader
    {
        public BinaryStream VolumeStream { get; set; }
        public ushort BlockSize { get; set; } = 0x800;
        public ushort GetBlockSize()
            => BlockSize;

        // Non original, just to init it
        public static DebugReader FromVolume(string volume)
        {
            var debugReader = new DebugReader();
            var fs = new FileStream(volume, FileMode.Open);
            debugReader.VolumeStream = new BinaryStream(fs);
            return debugReader;
        }

        public Entry TraversePathFindEntry(int parentID, string path)
        {
            Entry entry = null;

            foreach (var part in path.Split('/'))
            {
                entry = GetEntryOfPathPart(parentID, part, part.Length);

                if (((byte)entry.EntryType & 0x01) == 0)
                    return null;

                parentID = entry.ParentNode;
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
                int nextBlockIndex = blockInfo.SearchBlockIndex(entryInput, entryInput.Length);
                blockInfo.SwitchToNewIndex(nextBlockIndex);
            }

            // At that point we have the block of which the entry we're looking for is in

            if (!blockInfo.SearchEntry(entryInput, entryInput.Length, out int resultEntryIndex))
                return null; // return default entry
            else
            {
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
                return DecryptBlock(VolumeStream, 0x800 + beginOffset, endOffset - beginOffset);
            }
        }

        public int GetEntryOffset(int blockIndex)
        {
            VolumeStream.Position = 0x800 + TocHeader.HeaderSize + (blockIndex * 4);
            return VolumeStream.ReadInt32();
        }

        public int GetEntryOffsetSecure(int blockIndex)
        {
            VolumeStream.Position = 0x800 + TocHeader.HeaderSize + (blockIndex * 4);
            return VolumeStream.ReadInt32() ^ blockIndex * Volume.OffsetCryptKey + Volume.OffsetCryptKey;
        }

        public bool IsSoFS()
        {
            VolumeStream.Position = 0x800;
            return VolumeStream.ReadInt32(ByteConverter.Big) == TocHeader.MagicValue;
        }

        public static int CompareEntries(byte[] entry1, int entry1Len, byte[] entry2, int entry2Len)
        {
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
                // return the first
                sr.Position = blockSize - 4;
                return sr.ReadInt32();
            }

            // Last
            sr.Position = blockSize - (realEntryCount * 0x08);
            entryOffset = sr.ReadInt16();
            entryLength = sr.ReadInt16();
            sr.Position = entryOffset;
            entryIndexer = sr.ReadBytes(entryLength);
            diff = DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength);

            if (diff < 1)
            {
                int min = 0;
                do
                {
                    int max = realEntryCount;
                    int mid = (min + max) / 2;
                    while (true)
                    {
                        if (max - min < 8)
                        {
                            if (max < min)
                                return -1;

                            int off = (blockSize - (min * 0x08));
                            int t = off;

                            while (max > min)
                            {
                                // LAB_0067fad0
                                t -= 8;
                                sr.Position = t;
                                entryOffset = sr.ReadInt16();

                                sr.Position = blockSize - (min * 0x08) + off + t;
                                sr.Position += 2;
                                entryLength = sr.ReadInt16();

                                sr.Position = entryOffset;
                                entryIndexer = sr.ReadBytes(entryLength);
                                diff = DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength);

                                if (diff <= 0)
                                    return sr.ReadInt32();

                                min++;
                            }

                            return -1;
                        }
                        else
                        {
                            // Get previous to mid
                            sr.Position = blockSize + (mid * 0x08);
                            sr.Position -= 8;

                            entryOffset = sr.ReadInt16();
                            entryLength = sr.ReadInt16();
                            sr.Position = entryOffset;
                            entryIndexer = sr.ReadBytes(entryLength);
                            diff = DebugReader.CompareEntries(input, inputLen, entryIndexer, entryLength);

                            if (diff == 0)
                                break;// LAB_0067f970
                            else if (diff < 0)
                            {
                                // Got it
                                return sr.ReadInt32();
                            }

                            min = mid;
                            mid += max / 2;
                        }
                    }
                } while (true);
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
                    result = mid;
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
