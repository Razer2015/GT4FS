using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

using System.Buffers;
using System.Buffers.Binary;

using ICSharpCode.SharpZipLib.Zip.Compression;

namespace GT.Shared.Helpers
{
    public static class Compression
    {
        public static byte[] Compress(byte[] data)
        {
            MemoryStream output = new MemoryStream();
            using (DeflateStream dstream = new DeflateStream(output, CompressionLevel.Optimal))
            {
                dstream.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        public static byte[] Decompress(byte[] data)
        {
            using MemoryStream input = new MemoryStream(data);
            using MemoryStream output = new MemoryStream();
            using (DeflateStream dstream = new DeflateStream(input, CompressionMode.Decompress))
            {
                dstream.CopyTo(output);
            }
            return output.ToArray();
        }

        /// <summary>
        /// Compresses input data (PS2ZIP-like) from one stream to another.
        /// </summary>
        /// <param name="data"></param>
        /// <returns>Length of the compressed data.</returns>
        public static long PS2ZIPCompressInto(Stream input, Stream output)
        {
            long basePos = output.Position;
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header, 0xC5EEF7FFu);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], -(int)input.Length);

            
            output.Write(header);
            
            input.Position = 0;
            var inputBytes = ArrayPool<byte>.Shared.Rent((int)input.Length);
            using var ms = new MemoryStream(inputBytes);
            input.CopyTo(ms);
            ms.Position = 0;

            /* As always, for some reason, default deflate stream seems to cause issues with the game..
            using (var dstream = new DeflateStream(output, CompressionMode.Compress, leaveOpen: true))
                dstream.Write(inputBytes, 0, (int)input.Length);
            */

            var d = new Deflater(Deflater.DEFAULT_COMPRESSION, true);
            d.SetInput(inputBytes);
            d.Finish();

            int count = d.Deflate(inputBytes);
            output.Write(inputBytes, 0, count);

            ArrayPool<byte>.Shared.Return(inputBytes);

            return count + 8; // output.Position - basePos;
        }
    }
}
