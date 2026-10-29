using System.IO.Compression;
using System.Text;
using Bubo.Media.Infrastructure;

static class CodecFixtures
{
    public static void Run(Action<bool,string> check)
    {
        var root=Path.Combine(AppContext.BaseDirectory,"test-fixtures","codecs-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        void Reject(Action action,string name)
        {
            try{action();}catch(Exception e) when(e is InvalidOperationException or IOException){check(true,name);return;}
            throw new InvalidOperationException("Accepted "+name);
        }
        var pixels=Enumerable.Range(0,64*64*3).Select(i=>(byte)(i%251)).ToArray();
        var raw=Path.Combine(root,"owned.rgb");File.WriteAllBytes(raw,pixels);var png=Path.Combine(root,"owned.png");ImagePng.Write(raw,png,64,64,3);
        check(ImagePng.Inspect(png)==(64,64),"encoded PNG dimensions and header CRC validated");
        using(var stream=File.OpenRead(png))
        using(var reader=new BinaryReader(stream))
        {
            stream.Position=33;var length=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(reader.ReadBytes(4));
            check(Encoding.ASCII.GetString(reader.ReadBytes(4))=="IDAT","PNG contains a compressed data chunk");
            using var compressed=new MemoryStream(reader.ReadBytes(length));using var inflater=new ZLibStream(compressed,CompressionMode.Decompress);using var restored=new MemoryStream();inflater.CopyTo(restored);
            var rows=restored.ToArray();check(rows.Length==64*(64*3+1) && Enumerable.Range(0,64).All(y=>rows[y*193]==0 && rows.AsSpan(y*193+1,192).SequenceEqual(pixels.AsSpan(y*192,192))),"independently decompressed PNG retains all source pixels");
        }
        Reject(()=>ImagePng.Write(raw,png,64,64,3),"staged image cannot be overwritten");
        File.WriteAllBytes(raw,pixels[..^1]);Reject(()=>ImagePng.Write(raw,Path.Combine(root,"partial.png"),64,64,3),"truncated native pixel buffer rejected");
        string Wave(string name,float[] samples,bool oddTail=false,bool partialTail=false)
        {
            var path=Path.Combine(root,name);using var stream=new FileStream(path,FileMode.CreateNew);using var writer=new BinaryWriter(stream,Encoding.ASCII,true);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));writer.Write(0u);writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));writer.Write(16u);writer.Write((ushort)3);writer.Write((ushort)2);writer.Write(44100u);writer.Write(352800u);writer.Write((ushort)8);writer.Write((ushort)32);
            writer.Write(Encoding.ASCII.GetBytes("data"));writer.Write((uint)(samples.Length*4));foreach(var sample in samples)writer.Write(sample);
            if(oddTail){writer.Write(Encoding.ASCII.GetBytes("JUNK"));writer.Write(1u);writer.Write((byte)0);}
            if(partialTail)writer.Write((byte)0);
            var length=stream.Length;stream.Position=4;writer.Write((uint)(length-8));return path;
        }
        var wave=Wave("measured.wav",[0,.5f,-1,1.25f]);var measures=WaveInspection.Read(wave,4096);
        check(measures.Frames==2 && measures.Peak==1.25 && measures.ClippedFraction==.5 && measures.SilentFraction==.25,"independent waveform measurement preserves clipping, silence and original amplitude");
        Reject(()=>WaveInspection.Read(Wave("nonfinite.wav",[float.NaN,0]),4096),"nonfinite audio rejected");
        Reject(()=>WaveInspection.Read(Wave("odd.wav",[0,0],oddTail:true),4096),"missing odd-chunk padding rejected");
        Reject(()=>WaveInspection.Read(Wave("partial.wav",[0,0],partialTail:true),4096),"trailing partial RIFF chunk rejected");
        Reject(()=>WaveInspection.Read(wave,20),"actual waveform byte budget enforced before samples");
    }
}
