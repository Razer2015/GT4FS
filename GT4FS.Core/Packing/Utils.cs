﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace GT4FS.Core;

public class Utils
{
    public static int Align(int offset, int alignment)
    {
        var newPos = (-offset % alignment + alignment) % alignment;
        offset += newPos;
        return offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe static void XorEncryptFast(Span<byte> data, byte key)
    {
        if (Avx2.IsSupported)
        {
            Vector256<byte> keyB = Vector256.Create(key);
            Span<Vector256<byte>> blocks = MemoryMarshal.Cast<byte, Vector256<byte>>(data);

            for (int i = 0; i < blocks.Length; i++)
            {
                var x = Avx2.Xor(blocks[i], keyB);
                blocks[i] = x;
            }
        }
        else if (Sse2.IsSupported)
        {
            Vector128<byte> keyB = Vector128.Create(key);
            Span<Vector128<byte>> blocks = MemoryMarshal.Cast<byte, Vector128<byte>>(data);

            for (int i = 0; i < blocks.Length; i++)
            {
                var x = Sse2.Xor(blocks[i], keyB);
                blocks[i] = x;
            }

        }
        else
        {
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(data[i] ^ key);
            }
        }
    }

    // https://stackoverflow.com/a/4975942
    private static string[] sizesuf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" }; //Longs run out around EB
    public static string BytesToString(long byteCount)
    {
        if (byteCount == 0)
            return "0" + sizesuf[0];
        long bytes = Math.Abs(byteCount);
        int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
        double num = Math.Round(bytes / Math.Pow(1024, place), 1);
        return (Math.Sign(byteCount) * num).ToString() + sizesuf[place];
    }
}
