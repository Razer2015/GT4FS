using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GT.Shared {
    public class Util {
        public struct uint128_t // Untested
        {
            public ulong hi;
            public ulong lo;
        }

        public static uint HI32(ulong x) // Untested
        {
            return (uint)(x >> 32);
        }

        public static uint LO32(ulong x) // Untested
        {
            return (uint)(x & 0xFFFFFFFFu);
        }

        public static uint128_t MUL64(ulong a, ulong b) // Untested
        {
            ulong ah = HI32(a), bh = HI32(b);
            ulong al = LO32(a), bl = LO32(b);
            ulong ahbl = ah * bl;
            ulong albh = al * bh;
            ulong albl = al * bl;
            ulong ahbh = ah * bh;
            ulong mid = HI32(albl) + LO32(ahbl) + LO32(albh);

            uint128_t result;
            result.hi = ahbh + HI32(ahbl) + HI32(albh) + HI32(mid);
            result.lo = LO32(albl) + (mid << 32);

            return result;
        }

        public static uint Bitmask32(int begin, int end) // Untested
        {
            uint ones = unchecked((uint)(-1));
            uint mask_begin = ones >> begin;
            uint mask_end = ones << (31 - end);
            uint mask = begin <= end ? (mask_begin & mask_end) : (mask_begin | mask_end);
            return mask;
        }

        public static ulong Bitmask64(int begin, int end) // Untested
        {
            ulong ones = unchecked((ulong)(-1));
            ulong mask_begin = ones >> begin;
            ulong mask_end = ones << (63 - end);
            ulong mask = begin <= end ? (mask_begin & mask_end) : (mask_begin | mask_end);
            return mask;
        }

        public static uint ROTL(uint x, int n) {
            uint result = (x << n) | (x >> (32 - n));
            return result;
        }

        public static uint ROTR(uint x, int n) // Untested
        {
            uint result = (x >> n) | (x << (32 - n));
            return result;
        }

        public static uint RLWINMBitmask(uint rs, uint sh, uint bm) // Untested
        {
            uint ra = ROTL(rs, (int)sh) & bm;
            return ra;
        }

        public static uint RLWINM(uint rs, uint sh, int mb, int me) // Untested
        {
            uint mask = Bitmask32(mb, me);
            uint ra = RLWINMBitmask(rs, sh, mask);
            return ra;
        }

        public static uint RLWIMI(uint ra, uint rs, uint sh, int mb, int me) // Untested
        {
            uint mask = Bitmask32(mb, me);
            uint nra = (ra & ~mask) | ROTL(rs, (int)sh) & mask;
            return nra;
        }

        public static uint CLRLSLWI(uint rs, int b, int n) // Untested
        {
            System.Diagnostics.Debug.Assert(n <= b && b <= 31);
            uint ra = RLWINM(rs, (uint)n, b - n, 31 - n);
            return ra;
        }

        public static uint SLW(uint ra, uint rs, int rb) // Untested
        {
            int n = rb & 0x1F;
            ulong mask = (rb & 32) == 0 ? Bitmask64(32, 63 - n) : 0;
            uint nra = ROTL(rs, n) & (uint)mask;
            return nra;
        }

        public static Int32 SRAWI(uint rs, int b) // Untested
        {
            Int32 ra = (Int32)(rs) >> b;
            return ra;
        }

        public static ulong AlignUp(ulong address, ulong alignment) {
            return (address + (alignment - 1)) & ~((ulong)(alignment) - 1);
        }

        public static ulong AlignDown(ulong address, ulong alignment) {
            return address & ~((ulong)(alignment) - 1);
        }

        public static Int16 DataAtInt16(byte[] input, int offset) {
            MemoryStream ms = new MemoryStream(input);
            byte[] buffer = new byte[2];
            ms.Position = offset;
            ms.Read(buffer, 0, 2);
            return (BitConverter.ToInt16(buffer, 0));
        }

        public static ushort DataAtUInt16(byte[] input, int offset) {
            MemoryStream ms = new MemoryStream(input);
            byte[] buffer = new byte[2];
            ms.Position = offset;
            ms.Read(buffer, 0, 2);
            Array.Reverse(buffer);
            return (BitConverter.ToUInt16(buffer, 0));
        }

        public static Int32 DataAtInt32(byte[] input, int offset) {
            MemoryStream ms = new MemoryStream(input);
            byte[] buffer = new byte[4];
            ms.Position = offset;
            ms.Read(buffer, 0, 4);
            return (BitConverter.ToInt32(buffer, 0));
        }

        public static uint DataAtUInt32(byte[] input, int offset) {
            MemoryStream ms = new MemoryStream(input);
            byte[] buffer = new byte[4];
            ms.Position = offset;
            ms.Read(buffer, 0, 4);
            Array.Reverse(buffer);
            return (BitConverter.ToUInt32(buffer, 0));
        }

        public static Int64 DataAtInt64(byte[] input, int offset) {
            MemoryStream ms = new MemoryStream(input);
            byte[] buffer = new byte[8];
            ms.Position = offset;
            ms.Read(buffer, 0, 8);
            return (BitConverter.ToInt64(buffer, 0));
        }

        public static ulong DataAtUInt64(byte[] input, int offset) {
            MemoryStream ms = new MemoryStream(input);
            byte[] buffer = new byte[8];
            ms.Position = offset;
            ms.Read(buffer, 0, 8);
            Array.Reverse(buffer);
            return (BitConverter.ToUInt64(buffer, 0));
        }

        public static byte[] UInt16ToBarray(ushort val) {
            byte[] returnval = BitConverter.GetBytes(val);
            Array.Reverse(returnval);
            return (returnval);
        }

        public static ushort BarrayToUInt16(byte[] arr) {
            Array.Reverse(arr);
            ushort returnval = BitConverter.ToUInt16(arr, 0);
            Array.Reverse(arr);
            return (returnval);
        }

        public static byte[] UInt32ToBarray(uint val) {
            byte[] returnval = BitConverter.GetBytes(val);
            Array.Reverse(returnval);
            return (returnval);
        }

        public static uint BarrayToUInt32(byte[] arr) {
            Array.Reverse(arr);
            uint returnval = BitConverter.ToUInt32(arr, 0);
            Array.Reverse(arr);
            return (returnval);
        }

        public static byte[] UInt64ToBarray(ulong val) {
            byte[] returnval = BitConverter.GetBytes(val);
            Array.Reverse(returnval);
            return (returnval);
        }

        public static ulong BarrayToUInt64(byte[] arr) {
            Array.Reverse(arr);
            ulong returnval = BitConverter.ToUInt64(arr, 0);
            Array.Reverse(arr);
            return (returnval);
        }

        public static byte[] String2ByteArray(string word) {
            List<byte> bytelist = new List<byte>();

            char[] arr = word.ToCharArray(0, word.Length);
            foreach (char c in arr) {
                bytelist.Add(Convert.ToByte(c));
            }

            return bytelist.ToArray();
        }

        public static string ByteArray2String(byte[] arr) {
            MemoryStream mem = new MemoryStream(arr);
            StringBuilder SB = new StringBuilder("");

            int num2 = arr.Length;
            for (int index = 0; index < num2; ++index)
                SB.Append((char)mem.ReadByte());

            return SB.ToString();
        }

        public static EndianBinReader AdvancePointer(EndianBinReader reader, uint offset) {
            reader.BaseStream.Position = offset;
            return (reader);
        }

        public static void PackValueAndAdvance(EndianBinWriter writer, uint in_value) {
            uint mask = 0x80;
            byte[] buffer = new byte[0];
            if (in_value <= 0x7F) {
                byte value = (byte)in_value;
                writer.Write(value);
                return;
            }
            else if (in_value <= 0x3FFF) {
                ushort value = (ushort)in_value;
                byte[] temp = BitConverter.GetBytes(in_value).Reverse().ToArray();
                buffer = new byte[] { temp[2], temp[3] };
            }
            else if (in_value <= 0x1FFFFF) {
                uint value = in_value;
                byte[] temp = BitConverter.GetBytes(in_value).Reverse().ToArray();
                buffer = new byte[] { temp[1], temp[2], temp[3] };
            }
            else if (in_value <= 0xFFFFFFF) {
                uint value = in_value;
                buffer = BitConverter.GetBytes(in_value).Reverse().ToArray();
            }
            else if (in_value <= 0xFFFFFFFF) {
                uint value = in_value;
                byte[] temp = BitConverter.GetBytes(in_value).Reverse().ToArray();
                buffer = new byte[] { 0x00, temp[0], temp[1], temp[2], temp[3] };
            }
            else {
                throw new Exception("Somethings wrong with the pack_value_and_advance - function");
            }

            for (int i = 1; i < buffer.Length; i++) {
                buffer[0] += (byte)mask;
                mask = mask >> 1;
            }
            writer.Write(buffer);
        }

        public static uint ExtractValueAndAdvance(EndianBinReader reader) // Untested
        {
            uint value = reader.ReadByte();
            if ((value & 0x80) != 0) {
                uint mask = 0x80;
                do {
                    value = ((value - mask) << 8) + reader.ReadByte();
                    mask = mask << 7;
                } while ((value & mask) != 0);
            }
            return value;
        }

        public static uint ExtractValueAndAdvance(EndianBinReader reader, ref uint ptr) {
            uint p = 0;
            reader.BaseStream.Seek((ptr + p++), SeekOrigin.Begin);
            uint value = reader.ReadByte();
            if ((value & 0x80) != 0) {
                uint mask = 0x80;
                do {
                    reader.BaseStream.Seek((ptr + p++), SeekOrigin.Begin);
                    value = ((value - mask) << 8) + reader.ReadByte();
                    mask = mask << 7;
                } while ((value & mask) != 0);
            }
            ptr += p;
            return value;
        }

        public static uint ExtractValueAndAdvance(byte[] data, ref uint ptr) {
            uint p = 0;
            uint value = data[ptr + p++];
            if ((value & 0x80) != 0) {
                uint mask = 0x80;
                do {
                    value = ((value - mask) << 8) + data[ptr + p++];
                    mask = mask << 7;
                } while ((value & mask) != 0);
            }
            ptr += p;
            return value;
        }

        public static void PackTwelveBits(EndianBinWriter writer, uint ptr_data, uint offset) {
            writer.BaseStream.Seek((int)((offset * 16 - offset * 4) / 8), SeekOrigin.Begin);
            if ((offset & 0x1) == 0) {
                ptr_data *= 16;
                writer.Write((ushort)(ptr_data));
            }
            else {
                int val = writer.BaseStream.ReadByte(); writer.BaseStream.Position -= 1;
                val = val << 8;
                writer.Write((ushort)(ptr_data + val));
            }
        }

        public static ushort ExtractTwelveBits(EndianBinReader reader, uint offset) {
            reader.BaseStream.Position += ((offset * 16 - offset * 4) / 8);
            ushort result = reader.ReadUInt16();
            if ((offset & 0x1) == 0)
                result /= 16;
            return (ushort)(result & 0xFFF);
        }

        public static ushort ExtractTwelveBits(EndianBinReader reader, uint ptr_data, uint offset) {
            reader.BaseStream.Seek((int)(ptr_data + (offset * 16 - offset * 4) / 8), SeekOrigin.Begin);
            ushort result = reader.ReadUInt16();
            if ((offset & 0x1) == 0)
                result /= 16;
            return (ushort)(result & 0xFFF);
        }

        public static ushort ExtractTwelveBits(byte[] data, uint ptr_data, uint offset) {
            ushort result = DataAtUInt16(data, (int)(ptr_data + (offset * 16 - offset * 4) / 8));
            if ((offset & 0x1) == 0)
                result /= 16;
            return (ushort)(result & 0xFFF);
        }

        public static string ExtractStringAtOffset(ref EndianBinReader reader, uint offset) {
            reader.BaseStream.Position += offset;
            byte Length = reader.ReadByte();
            return Encoding.ASCII.GetString(reader.ReadBytes((int)Length)).TrimEnd('\0');
        }

        public static string ExtractStringAtOffset(EndianBinReader reader, uint offset) {
            reader.BaseStream.Seek(offset, SeekOrigin.Begin);
            byte Length = reader.ReadByte();
            return Encoding.ASCII.GetString(reader.ReadBytes(Length), 0, Length);
        }

        public static string ExtractStringAtOffset(byte[] data, uint offset) {
            return Encoding.ASCII.GetString(data, (int)(offset + 1), data[offset]);
        }
    }
}