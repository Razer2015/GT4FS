namespace GT.Shared {
    public class Consts {
        public const uint kVOLUME_HEADER_MAGIC = 0x5B745162u;
        public const uint kVOLUME_HEADER_SIZE = 0xA0;

        public const uint kVOLUME_SEGMENT_MAGIC = 0x5B74516Eu;
        public const uint kVOLUME_SEGMENT_HEADER_SIZE = 0x08;
        public const uint kVOLUME_SEGMENT_SIZE = 0x800;

        public const string CONFIG_EXTENSION = ".gttoolconfig";
    }
}
