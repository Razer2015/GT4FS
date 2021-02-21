using System;
using System.IO;

using Syroot.BinaryData;

namespace GT4FS.Core {
    public struct TocHeader
    {
        public const int HeaderSize = 0x40;
        public const string Magic = "RoFS";
        public const int MagicValue = -0x526f4654; // RoFT.. SoFS.. Which one?
        public const int Version3_1 = 0x00_03_00_01;

        public uint Version { get; set; }

        public int Length { get; set; }
        public int PageCount { get; set; }
        public ushort PageLength { get; set; }
        public ushort EntryCount { get; set; }
        public long DataOffset { get; set; }

        public TocHeader(BinaryStream reader, long baseOffset)
        {
            reader.BaseStream.Seek(baseOffset, SeekOrigin.Begin);
            if (reader.ReadInt32(ByteConverter.Big) != MagicValue)
                throw new Exception("Why are you trying to extract a VOL that's not meant to be extracted with this tool? Dummy!");

            Version = reader.ReadUInt32();
            Length = reader.ReadInt32();
            PageCount = reader.ReadInt32();
            PageLength = reader.ReadUInt16();
            EntryCount = reader.ReadUInt16();
            DataOffset = (baseOffset + PageCount * PageLength);
        }

        public void Write(BinaryStream writer)
        {
            writer.WriteInt32(MagicValue, ByteConverter.Big);
            writer.WriteUInt32(Version);

            writer.WriteInt32(Length);
            writer.WriteInt32(PageCount);
            writer.WriteUInt16(PageLength);
            writer.WriteUInt16(EntryCount);

            writer.Seek(HeaderSize, SeekOrigin.Begin);
        }
    }
}
