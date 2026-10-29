using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Bubo.Media.Infrastructure;

/// <summary>Small bounded RGB/RGBA encoder; model pixels are not altered or reviewed here.</summary>
public static class ImagePng
{
    static readonly byte[] Signature=[137,80,78,71,13,10,26,10];
    public static void Write(string raw,string output,int width,int height,int channels)
    {
        if(width is <64 or >2048 || height is <64 or >2048 || channels is not (3 or 4))throw new InvalidOperationException("Unsupported native pixel dimensions");
        var length=checked(width*height*channels);
        if(new FileInfo(raw).Length!=length)throw new InvalidOperationException("Native pixel length differs from dimensions");
        using var pixels=File.OpenRead(raw);
        using var compressed=new MemoryStream();
        using(var zlib=new ZLibStream(compressed,CompressionLevel.SmallestSize,true))
        {
            var row=new byte[width*channels];
            for(var y=0;y<height;y++){pixels.ReadExactly(row);zlib.WriteByte(0);zlib.Write(row);}
        }
        using var target=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        target.Write(Signature);var header=new byte[13];BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0,4),width);BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4,4),height);
        header[8]=8;header[9]=(byte)(channels==3?2:6);Chunk(target,"IHDR",header);Chunk(target,"IDAT",compressed.ToArray());Chunk(target,"IEND",[]);target.Flush(true);
    }
    public static (int Width,int Height) Inspect(string path)
    {
        using var stream=File.OpenRead(path);var header=new byte[33];stream.ReadExactly(header);
        if(!header.AsSpan(0,8).SequenceEqual(Signature) || BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8,4))!=13
            || Encoding.ASCII.GetString(header,12,4)!="IHDR" || header[24]!=8 || header[25] is not (2 or 6)
            || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(29,4))!=Crc(header.AsSpan(12,17)))throw new InvalidOperationException("Invalid staged PNG header");
        return(BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16,4)),BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20,4)));
    }
    static void Chunk(Stream output,string kind,byte[] data)
    {
        Span<byte> number=stackalloc byte[4];BinaryPrimitives.WriteInt32BigEndian(number,data.Length);output.Write(number);
        var type=Encoding.ASCII.GetBytes(kind);output.Write(type);output.Write(data);
        var crc=new byte[type.Length+data.Length];type.CopyTo(crc,0);data.CopyTo(crc,type.Length);BinaryPrimitives.WriteUInt32BigEndian(number,Crc(crc));output.Write(number);
    }
    static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc=0xffffffff;foreach(var value in data){crc^=value;for(var i=0;i<8;i++)crc=(crc>>1)^(0xedb88320u&(uint)-(int)(crc&1));}return ~crc;
    }
}
