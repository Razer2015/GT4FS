using System;
using System.Text;

using Syroot.BinaryData;

namespace GT4FS.Core 
{
    public class NodeEntry
    {
        public int ParentNode { get; set; }
        public string Name { get; set; }
        public VolumeEntryType Flag { get; set; }
        public uint NodeId { get; set; }
        public uint PageOffset { get; set; }
        public DateTime ModifiedDate { get; set; }
        public uint PackedSize { get; set; }
        public uint RealSize { get; set; }

        public NodeEntry(BinaryStream reader)
        {
            ParentNode = reader.ReadInt32(ByteConverter.Big); // This is big endian?
            Name = ReadName(reader);
            Flag = (VolumeEntryType)reader.Read1Byte();

            switch (Flag)
            {
                case VolumeEntryType.Directory:
                    NodeId = reader.ReadUInt32();
                    break;
                case VolumeEntryType.File:
                    PageOffset = reader.ReadUInt32();
                    ModifiedDate = reader.ReadDateTime(DateTimeCoding.CTime);
                    PackedSize = reader.ReadUInt32();
                    RealSize = PackedSize;
                    break;
                case VolumeEntryType.CompressedFile:
                    PageOffset = reader.ReadUInt32();
                    ModifiedDate = reader.ReadDateTime(DateTimeCoding.CTime);
                    PackedSize = reader.ReadUInt32();
                    RealSize = reader.ReadUInt32();
                    break;
                default:
                    throw new ArgumentOutOfRangeException($"Unknown flag {Flag}");
            }

            reader.Align(0x04);
        }

        private static string ReadName(BinaryStream reader)
        {
            var sb = new StringBuilder();

            // TODO: actually use the block's toc for this..
            while (true)
            {
                char c = (char)reader.Read1Byte();
                if ((byte)c <= 2)
                    break;
                sb.Append(c);
            }

            reader.BaseStream.Position--;
            reader.Align(0x04);

            return sb.ToString();
        }
    }

    public enum VolumeEntryType : byte
    {
        Directory,
        File,
        CompressedFile
    }
}
