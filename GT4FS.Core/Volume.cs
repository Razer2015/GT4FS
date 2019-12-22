using GT.Shared.Polyphony;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace GT4FS.Core {
    public class Volume {
        const ulong TOC31MAGIC = 0xAD90B9AC01000300;

        private long _baseOffset = 0;
        private readonly string _filePath;
        private TocHeader _tocHeader;
        private int _firstBlockOffset;
        private List<int> _offsets;
        private List<(long Offset, int Length)> _blocks;

        public Volume(string filePath) {
            _filePath = filePath;
        }

        public void Read() {
            using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read))
            using (var reader = new EndianBinReader(fs, EndianType.LITTLE_ENDIAN)) {
                _baseOffset = BaseOffset();
                _tocHeader = new TocHeader(reader, _baseOffset);

#if DEBUG
                // Debug write what has been decrypted so far (TOC header)
                using (var ms = new MemoryStream())
                using (var writer = new EndianBinWriter(ms)) {
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
        }

        /// <summary>
        ///     Search for the TOC 3.1 in the VOL file and return its offset if found
        /// </summary>
        private long BaseOffset() {
            using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read))
            using (var reader = new EndianBinReader(fs, EndianType.BIG_ENDIAN)) {
                for (int i = 0; i < Math.Min((reader.BaseStream.Length / 0x800), 10000); i++) {
                    reader.BaseStream.Seek(i * 0x800, SeekOrigin.Begin);
                    if (reader.ReadUInt64() == TOC31MAGIC) {
                        return i * 0x800;
                    }
                }
            }

            throw new Exception("TOC 3.1 wasn't found. Are you sure you have a correct VOL file?");
        }

        /// <summary>
        ///     Get block offset and length based on index (0 is garbage table?)
        /// </summary>
        /// <param name="index"></param>
        private (long Offset, int Length) GetBlock(int index) {
            if (_blocks == null || _blocks.Count <= 0) {
                ReadBlocks();
            }

            return _blocks[index];
        }

        /// <summary>
        ///     Return the file offset based on the page given by the TOC (Appends it to the TOC end offset)
        /// </summary>
        /// <param name="pageOffset"></param>
        private long GetFileOffset(int pageOffset) {
            return _tocHeader.DataOffset + (pageOffset * _tocHeader.PageLength);
        }

        private void ReadEntryOffsets() {
            _offsets = new List<int>();
            using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read))
            using (var reader = new EndianBinReader(fs, EndianType.LITTLE_ENDIAN)) {
                reader.BaseStream.Seek(_baseOffset + 0x40, SeekOrigin.Begin);
                var xorKey = 0x14ac327a;
                for (int i = 0; i < _tocHeader.EntryCount + 1; i++) {
                    var buffer = reader.ReadBytes(0x04);
                    var offset = BitConverter.ToInt32(PS2Zip.XorEncript(buffer, BitConverter.GetBytes(xorKey * (i + 1))), 0);
                    if (i == 0) {
                        _firstBlockOffset = offset;
                        continue;
                    }
                    _offsets.Add(offset);
                }
            }

#if DEBUG
            // Debug write what has been decrypted so far (TOC header and block offsets)
            using (var ms = new MemoryStream())
            using (var writer = new EndianBinWriter(ms)) {
                _tocHeader.Write(writer);
                writer.Write(_firstBlockOffset);
                foreach (var offset in _offsets) {
                    writer.Write(offset);
                }

                File.WriteAllBytes("offsetTable.bin", ms.ToArray());
            }
#endif
        }

        private void ReadBlocks() {
            _blocks = new List<(long Offset, int Length)>();
            using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read))
            using (var reader = new EndianBinReader(fs, EndianType.LITTLE_ENDIAN)) {
                _offsets.Aggregate(_firstBlockOffset, (acc, x) => {
                    _blocks.Add((_baseOffset + acc, x - acc));
                    return x;
                });
            }
        }

        private byte[] DecryptBlock(long offset, int length) {
            using (var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read))
            using (var reader = new EndianBinReader(fs, EndianType.LITTLE_ENDIAN)) {
                reader.BaseStream.Seek(offset, SeekOrigin.Begin);
                using (var decompressStream = new MemoryStream()) {
                    using (var decompressionStream = new DeflateStream(new MemoryStream(reader.ReadBytes(length)), CompressionMode.Decompress)) {
                        decompressionStream.CopyTo(decompressStream);
                        return PS2Zip.XorEncript(decompressStream.ToArray(), new byte[] { 0x55 });
                    }
                }
            }
        }

        private void DebugWriteBlocks() {
            using (var ms = new MemoryStream())
            using (var writer = new EndianBinWriter(ms)) {
                _tocHeader.Write(writer);
                writer.Write(_firstBlockOffset);
                foreach (var offset in _offsets) {
                    writer.Write(offset);
                }

                // Blocks
                foreach (var (Offset, Length) in _blocks) { // Block at index 0 is garbage?
                    writer.Write(DecryptBlock(Offset, Length));

                    //File.WriteAllBytes("block.bin", DecryptBlock(Offset, Length));
                }

                File.WriteAllBytes("toc.bin", ms.ToArray());
            }
        }
    }
}
