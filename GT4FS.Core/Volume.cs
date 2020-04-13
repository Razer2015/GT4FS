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
        private TocHeader _tocHeader;
        private int _firstBlockOffset;
        private List<int> _offsets;
        public List<(long Offset, int Length)> Blocks { get; set; }
        public EndianBinReader VOLReader { get; set; }

        public Volume(Stream stream) {
            VOLReader = new EndianBinReader(stream, EndianType.BIG_ENDIAN);
        }

        public void ReadVolume() {
            _baseOffset = BaseOffset();
            VOLReader.Endianess = EndianType.LITTLE_ENDIAN;
            _tocHeader = new TocHeader(VOLReader, _baseOffset);

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

        /// <summary>
        ///     Search for the TOC 3.1 in the VOL file and return its offset if found
        /// </summary>
        private long BaseOffset() {
            VOLReader.Endianess = EndianType.BIG_ENDIAN;
            for (int i = 0; i < Math.Min((VOLReader.BaseStream.Length / 0x800), 10000); i++) {
                VOLReader.BaseStream.Seek(i * 0x800, SeekOrigin.Begin);
                if (VOLReader.ReadUInt64() == TOC31MAGIC) {
                    return i * 0x800;
                }
            }

            throw new Exception("TOC 3.1 wasn't found. Are you sure you have a correct VOL file?");
        }

        /// <summary>
        ///     Get block offset and length based on index (0 is garbage table?)
        /// </summary>
        /// <param name="index"></param>
        private (long Offset, int Length) GetBlock(int index) {
            if (Blocks == null || Blocks.Count <= 0) {
                ReadBlocks();
            }

            return Blocks[index];
        }

        /// <summary>
        ///     Return the file offset based on the page given by the TOC (Appends it to the TOC end offset)
        /// </summary>
        /// <param name="pageOffset"></param>
        public long GetFileOffset(uint pageOffset) {
            return _tocHeader.DataOffset + (pageOffset * _tocHeader.PageLength);
        }

        private void ReadEntryOffsets() {
            _offsets = new List<int>();

            VOLReader.Endianess = EndianType.LITTLE_ENDIAN;
            VOLReader.BaseStream.Seek(_baseOffset + 0x40, SeekOrigin.Begin);
            var xorKey = 0x14ac327a;
            for (int i = 0; i < _tocHeader.EntryCount + 1; i++) {
                var buffer = VOLReader.ReadBytes(0x04);
                var offset = BitConverter.ToInt32(PS2Zip.XorEncript(buffer, BitConverter.GetBytes(xorKey * (i + 1))), 0);
                if (i == 0) {
                    _firstBlockOffset = offset;
                    continue;
                }
                _offsets.Add(offset);
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
            Blocks = new List<(long Offset, int Length)>();
            _offsets.Aggregate(_firstBlockOffset, (acc, x) => {
                Blocks.Add((_baseOffset + acc, x - acc));
                return x;
            });
        }

        public byte[] DecryptBlock(long offset, int length) {
            VOLReader.Endianess = EndianType.LITTLE_ENDIAN;
            VOLReader.BaseStream.Seek(offset, SeekOrigin.Begin);
            using (var decompressStream = new MemoryStream()) {
                using (var decompressionStream = new DeflateStream(new MemoryStream(VOLReader.ReadBytes(length)), CompressionMode.Decompress)) {
                    decompressionStream.CopyTo(decompressStream);
                    return PS2Zip.XorEncript(decompressStream.ToArray(), new byte[] { 0x55 });
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
                Directory.CreateDirectory("blocks");
                using (var sw = new StreamWriter("blocks.txt")) {
                    int index = 0;
                    foreach (var (Offset, Length) in Blocks) { // Block at index 0 is garbage?
                        var buffer = DecryptBlock(Offset, Length);
                        writer.Write(buffer);

                        using (var ms2 = new MemoryStream(buffer))
                        using (var reader = new EndianBinReader(ms2, EndianType.LITTLE_ENDIAN)) {
                            sw.WriteLine($"{index,4} - {reader.ReadInt16():X4} {reader.ReadInt16():X4} {reader.ReadInt32():X8} {reader.ReadInt32():X8} {reader.ReadInt32():X8}");
                        }

                        //File.WriteAllBytes($"blocks\\block_{index}.bin", buffer);
                        index++;
                    }
                }

                File.WriteAllBytes("toc.bin", ms.ToArray());
            }
        }
    }
}
