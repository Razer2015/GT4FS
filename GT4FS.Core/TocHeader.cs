using System;
using System.IO;

namespace GT4FS.Core {
    public struct TocHeader {
        public byte[] Magic { get; set; }
        public int Version { get; set; }
        public int Length { get; set; }
        public int PageCount { get; set; }
        public ushort PageLength { get; set; }
        public ushort EntryCount { get; set; }

        public TocHeader(EndianBinReader reader) {
            Magic = reader.ReadBytes(0x04);
            if ((Magic[0] != 0xAD) || (Magic[1] != 0x90) || (Magic[2] != 0xB9) || (Magic[3] != 0xAC))
                throw new Exception("Why are you trying to extract a VOL that's not meant to be extracted with this tool? Dummy!");

            Version = reader.ReadInt32();
            Length = reader.ReadInt32();
            PageCount = reader.ReadInt32();
            PageLength = reader.ReadUInt16();
            EntryCount = reader.ReadUInt16();
        }

        public void Write(EndianBinWriter writer) {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(Length);
            writer.Write(PageCount);
            writer.Write(PageLength);
            writer.Write(EntryCount);
            writer.Seek(0x40, SeekOrigin.Begin);
        }
    }
}
