using GT.Shared.Polyphony;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

using Syroot.BinaryData;

namespace GT4FS.Core {
    public class Volume
    {
        public const int DefaultBlockSize = 0x800;
        public const int OffsetCryptKey = 0x14ac327a;
        public static readonly byte[] DataCryptKey = new byte[] { 0x55 };

        private long _baseOffset = 0;
        private TocHeader _tocHeader;
        private int _firstBlockOffset;
        private List<int> _offsets;
        public List<(long Offset, int Length)> Blocks { get; set; }
        public BinaryStream VOLReader { get; set; }

        public Volume(Stream stream)
        {
            VOLReader = new BinaryStream(stream, ByteConverter.Little);
        }

        public void ReadVolume()
        {
            _baseOffset = BaseOffset();
            VOLReader.ByteConverter = ByteConverter.Little;
            _tocHeader = new TocHeader(VOLReader, _baseOffset);

#if DEBUG
            // Debug write what has been decrypted so far (TOC header)
            using (var ms = new MemoryStream())
            using (var writer = new BinaryStream(ms))
            {
                _tocHeader.Write(writer);

                File.WriteAllBytes("header.bin", ms.ToArray());
            }
#endif

            ReadEntryOffsets();
            ReadBlocks();
#if DEBUG
            // Debug write what has been decrypted so far (TOC header, Offset table, Blocks)
            DebugWriteBlocks();
#endif
        }

        /// <summary>
        ///     Search for the TOC 3.1 in the VOL file and return its offset if found
        /// </summary>
        private long BaseOffset()
        {
            VOLReader.ByteConverter = ByteConverter.Big;
            for (int i = 0; i < Math.Min((VOLReader.BaseStream.Length / DefaultBlockSize), 10000); i++)
            {
                VOLReader.BaseStream.Seek(i * DefaultBlockSize, SeekOrigin.Begin);
                int magic = VOLReader.ReadInt32();
                if ((magic == TocHeader.MagicValueEncrypted || magic == 0x526f4653)
                    && VOLReader.ReadInt32(ByteConverter.Little) == TocHeader.Version3_1)
                {
                    return i * DefaultBlockSize;
                }
            }

            throw new Exception("TOC 3.1 wasn't found. Are you sure you have a correct VOL file?");
        }

        /// <summary>
        ///     Get block offset and length based on index (0 is garbage table?)
        /// </summary>
        /// <param name="index"></param>
        private (long Offset, int Length) GetBlock(int index)
        {
            if (Blocks == null || Blocks.Count <= 0)
                ReadBlocks();

            return Blocks[index];
        }

        /// <summary>
        ///     Return the file offset based on the page given by the TOC (Appends it to the TOC end offset)
        /// </summary>
        /// <param name="pageOffset"></param>
        public long GetFileOffset(uint pageOffset)
            => _tocHeader.DataOffset + (pageOffset * _tocHeader.PageLength);

        private void ReadEntryOffsets()
        {
            _offsets = new List<int>();

            VOLReader.ByteConverter = ByteConverter.Little;
            VOLReader.BaseStream.Seek(_baseOffset + TocHeader.HeaderSize, SeekOrigin.Begin);
            for (int i = 0; i < _tocHeader.EntryCount + 1; i++)
            {
                byte[] buffer = VOLReader.ReadBytes(0x04);

                int offset;
                if (_tocHeader.Encrypted)
                    offset = BitConverter.ToInt32(PS2Zip.XorEncript(buffer, BitConverter.GetBytes(OffsetCryptKey * (i + 1))), 0);
                else
                    offset = BitConverter.ToInt32(buffer, 0);

                if (i == 0)
                {
                    _firstBlockOffset = offset;
                    continue;
                }
                _offsets.Add(offset);
            }

#if DEBUG
            // Debug write what has been decrypted so far (TOC header and block offsets)
            using (var ms = new MemoryStream())
            using (var writer = new BinaryStream(ms))
            {
                _tocHeader.Write(writer);
                writer.Write(_firstBlockOffset);
                foreach (var offset in _offsets)
                    writer.Write(offset);

                File.WriteAllBytes("offsetTable.bin", ms.ToArray());
            }
#endif
        }

        private void ReadBlocks()
        {
            Blocks = new List<(long Offset, int Length)>();
            _offsets.Aggregate(_firstBlockOffset, (acc, x) =>
            {
                Blocks.Add((_baseOffset + acc, x - acc));
                return x;
            });
        }

        public byte[] GetBlock(long offset, int length)
        {
            VOLReader.ByteConverter = ByteConverter.Little;
            VOLReader.BaseStream.Seek(offset, SeekOrigin.Begin);
            using (var decompressStream = new MemoryStream())
            {
                using (var decompressionStream = new DeflateStream(new MemoryStream(VOLReader.ReadBytes(length)), CompressionMode.Decompress))
                {
                    decompressionStream.CopyTo(decompressStream);
                    if (_tocHeader.Encrypted)
                        return PS2Zip.XorEncript(decompressStream.ToArray(), DataCryptKey);
                    else
                        return decompressStream.ToArray();
                }
            }
        }

        private void DebugWriteBlocks()
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryStream(ms))
            {
                _tocHeader.Write(writer);
                writer.Write(_firstBlockOffset);
                foreach (var offset in _offsets)
                    writer.Write(offset);

                // Blocks
                Directory.CreateDirectory("blocks");
                using (var sw = new StreamWriter("blocks.txt"))
                {
                    int index = 0;
                    foreach (var (Offset, Length) in Blocks)
                    { // Block at index 0 is garbage?
                        var buffer = GetBlock(Offset, Length);
                        writer.Write(buffer);

                        using (var ms2 = new MemoryStream(buffer))
                        using (var reader = new BinaryStream(ms2, ByteConverter.Big))
                            sw.WriteLine($"{index,4} - {reader.ReadInt16():X4} {reader.ReadInt16():X4} {reader.ReadInt32():X8} {reader.ReadInt32():X8} {reader.ReadInt32():X8}");

                        //File.WriteAllBytes($"blocks\\block_{index}.bin", buffer);
                        index++;
                    }
                }

                File.WriteAllBytes("toc.bin", ms.ToArray());
            }
        }
    }

    public enum GameVolumeType
    {
        GTHD,
        GT4,
        GT4_MX5_DEMO,
        GT4_FIRST_PREV,
        GT4_ONLINE,
        TT,
        TT_DEMO,
        CUSTOM,
        Unknown,
    }
}
