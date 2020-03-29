using System;
using System.Collections.Generic;
using System.Text;

namespace GT4FS.Core {
    public class NodeEntry {
        public int ParentNode { get; set; }
        public string Name { get; set; }
        public byte Flag { get; set; } // 0 = Dir, 1 = File, 2 = Compressed file
        public ushort NodeID { get; set; }
        public uint PageOffset { get; set; }
        public DateTime ModifiedDate { get; set; }
        public uint PackedSize { get; set; }
        public uint RealSize { get; set; }

        public NodeEntry(EndianBinReader reader) {
            ParentNode = reader.ReadInt32(GT.Shared.Polyphony.EndianType.BIG_ENDIAN); // This is big endian?
            Name = ReadName(reader);
            Flag = reader.ReadByte();

            switch (Flag) {
                case 0x00:
                    NodeID = reader.ReadUInt16();
                    reader.BaseStream.Position += 5;
                    break;
                case 0x01:
                    PageOffset = reader.ReadUInt32();
                    ModifiedDate = DateTimeOffset.FromUnixTimeSeconds(reader.ReadUInt32()).UtcDateTime;
                    PackedSize = reader.ReadUInt32();
                    RealSize = PackedSize;
                    reader.BaseStream.Position += 3;
                    break;
                case 0x02:
                    PageOffset = reader.ReadUInt32();
                    ModifiedDate = DateTimeOffset.FromUnixTimeSeconds(reader.ReadUInt32()).UtcDateTime;
                    PackedSize = reader.ReadUInt32();
                    RealSize = reader.ReadUInt32();
                    reader.BaseStream.Position += 3;
                    break;
                default:
                    throw new ArgumentOutOfRangeException($"Unknown flag {Flag}");
            }
        }

        private string ReadName(EndianBinReader reader) {
            var sb = new StringBuilder();
            do {
                sb.Append(Encoding.UTF8.GetString(reader.ReadBytes(0x04)));
            }
            while (((reader.PeekChar() >> 2) & 0xFF) > 0);

            return sb.ToString().TrimEnd('\0');
        }
    }
}
