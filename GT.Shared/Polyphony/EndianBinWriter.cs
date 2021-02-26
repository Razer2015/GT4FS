using System;
using System.IO;

public class EndianBinWriter : BinaryWriter
{
    public EndianBinWriter(Stream stream) : base(stream)
    {
    }

    public override void Write(double data)
    {
        byte[] bytes = BitConverter.GetBytes(data);
        Array.Reverse(bytes);
        base.Write(bytes);
    }

    public override void Write(short data)
    {
        byte[] bytes = BitConverter.GetBytes(data);
        Array.Reverse(bytes);
        base.Write(bytes);
    }

    public override void Write(int data)
    {
        byte[] bytes = BitConverter.GetBytes(data);
        Array.Reverse(bytes);
        base.Write(bytes);
    }

    public override void Write(long data)
    {
        byte[] bytes = BitConverter.GetBytes(data);
        Array.Reverse(bytes);
        base.Write(bytes);
    }

    public override void Write(float data)
    {
        byte[] bytes = BitConverter.GetBytes(data);
        Array.Reverse(bytes);
        base.Write(bytes);
    }

    public override void Write(ushort data)
    {
        byte[] bytes = BitConverter.GetBytes(data);
        Array.Reverse(bytes);
        base.Write(bytes);
    }

    public override void Write(uint data)
    {
        byte[] bytes = BitConverter.GetBytes(data);
        Array.Reverse(bytes);
        base.Write(bytes);
    }

    public override void Write(ulong data)
    {
        byte[] bytes = BitConverter.GetBytes(data);
        Array.Reverse(bytes);
        base.Write(bytes);
    }
}

