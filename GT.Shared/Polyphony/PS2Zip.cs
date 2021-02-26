using Ionic.Zlib;
using System;
using System.IO;
using System.Text;

public class PS2Zip {
    private const int ID = -528699;

    private static void debugOutputBytes(byte[] data) {
        string str = string.Empty;
        foreach (byte bytE in data) {
            int dat = Convert.ToInt32(bytE);
            str = str + string.Format("{0:X2}", dat) + " ";
        }
        Console.WriteLine("## src binary size:" + data.Length);
        Console.WriteLine(str);
    }

    public static byte[] Deflate(byte[] data) {
        byte[] destinationArray = null;
        byte[] sourceArray = null;
        int length = data.Length;
        try {
            sourceArray = ZlibCodecCompress(data);
        }
        catch (Exception exception) {
            Console.WriteLine("## error" + exception.ToString());
        }
        try {
            int length_ = -length;
            byte[] buffer1 = new byte[] { 0xc5, 0xee, 0xf7, 0xff, 0, 0, 0, 0 };
            buffer1[4] = (byte)length_;
            buffer1[5] = (byte)(length_ >> 8);
            buffer1[6] = (byte)(length_ >> 0x10);
            buffer1[7] = (byte)(length_ >> 0x18);
            byte[] buffer4 = buffer1;
            destinationArray = new byte[buffer4.Length + sourceArray.Length];
            Array.Copy(buffer4, destinationArray, buffer4.Length);
            Array.Copy(sourceArray, 0, destinationArray, buffer4.Length, sourceArray.Length);
        }
        catch (Exception exception2) {
            Console.WriteLine("## error" + exception2.ToString());
        }
        return destinationArray;//xor(destinationArray);
    }

    public static byte[] Inflate(byte[] data) {
        byte[] buffer = data;

        byte[] decompressed = new byte[buffer.Length - 8];
        Buffer.BlockCopy(buffer, 8, decompressed, 0, decompressed.Length);

        buffer = PS2Zip.ZlibCodecDecompress(decompressed);
        return (buffer);
    }


    public static byte[] xor(byte[] data) {
        string TedKey = null;
        TedKey = "E25geirEPHpc4WG2FnzacMqru";
        return XorEncript(data, TedKey);
    }

    public static byte[] XorEncript(byte[] data, string key) {
        byte[] bytes = Encoding.ASCII.GetBytes(key);
        byte[] buffer2 = new byte[data.Length];
        int num = 0;
        for (int i = 0; i < data.Length; i++) {
            if (num < bytes.Length) {
                num++;
            }
            else {
                num = 1;
            }
            buffer2[i] = (byte)(data[i] ^ bytes[num - 1]);
        }
        return buffer2;
    }

    public static byte[] XorEncript(byte[] data, byte[] key) {
        byte[] buffer = new byte[data.Length];
        int index = 0;
        for (int i = 0; i < data.Length; i++) {
            if (index < key.Length) {
                index++;
            }
            else {
                index = 1;
            }
            buffer[i] = (byte)(data[i] ^ key[index - 1]);
        }
        return buffer;
    }

    public static byte[] ZlibCodecCompress(byte[] data) {
        int buffer_size = 0x800;
        byte[] buffer = new byte[buffer_size];
        int length = data.Length;
        bool flag = false;
        using (MemoryStream stream = new MemoryStream()) {
            ZlibCodec codec = new ZlibCodec();
            codec.InitializeDeflate(CompressionLevel.Default, flag);
            codec.WindowBits = -15;
            codec.Strategy = CompressionStrategy.Default;
            codec.InputBuffer = data;
            codec.AvailableBytesIn = length;
            codec.NextIn = 0;
            codec.OutputBuffer = buffer;
            FlushType[] typeArray1 = new FlushType[2];
            typeArray1[1] = FlushType.Finish;
            foreach (FlushType type in typeArray1) {
                int count = 0;
                do {
                    codec.AvailableBytesOut = buffer_size;
                    codec.NextOut = 0;
                    codec.Deflate(type);
                    count = buffer_size - codec.AvailableBytesOut;
                    if (count > 0) {
                        stream.Write(buffer, 0, count);
                    }
                }
                while (((type == FlushType.None) && ((codec.AvailableBytesIn != 0) || (codec.AvailableBytesOut == 0))) || ((type == FlushType.Finish) && (count != 0)));
            }
            codec.EndDeflate();
            stream.Flush();
            return stream.ToArray();
        }
    }

    private static byte[] ZlibCodecDecompress(byte[] data) {
        int buffer_size = 0x800;
        byte[] buffer = new byte[buffer_size];
        bool flag = false;
        using (MemoryStream stream = new MemoryStream()) {
            ZlibCodec codec = new ZlibCodec();
            codec.InitializeInflate(flag);
            codec.InputBuffer = data;
            codec.AvailableBytesIn = data.Length;
            codec.NextIn = 0;
            codec.OutputBuffer = buffer;
            FlushType[] typeArray1 = new FlushType[2];
            typeArray1[1] = FlushType.Finish;
            foreach (FlushType type in typeArray1) {
                int count = 0;
                do {
                    codec.AvailableBytesOut = buffer_size;
                    codec.NextOut = 0;
                    codec.Inflate(type);
                    count = buffer_size - codec.AvailableBytesOut;
                    if (count > 0) {
                        stream.Write(buffer, 0, count);
                    }
                }
                while (((type == FlushType.None) && ((codec.AvailableBytesIn != 0) || (codec.AvailableBytesOut == 0))) || ((type == FlushType.Finish) && (count != 0)));
            }
            codec.EndInflate();
            return stream.ToArray();
        }
    }
}
