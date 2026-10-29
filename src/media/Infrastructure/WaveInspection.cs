using System.Text;

namespace Bubo.Media.Infrastructure;

public sealed record WaveMeasurements(int SampleRate,int Channels,long Frames,double DurationSeconds,double Peak,double Rms,double ClippedFraction,double SilentFraction);

public static class WaveInspection
{
    public static WaveMeasurements Read(string path,long maximumBytes)
    {
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length is <44 || input.Length>maximumBytes)throw new InvalidOperationException("Waveform exceeds its declared byte budget");
        using var reader=new BinaryReader(input,Encoding.ASCII,true);
        if(Encoding.ASCII.GetString(reader.ReadBytes(4))!="RIFF")throw new InvalidOperationException("Expected bounded RIFF waveform");
        var riff=reader.ReadUInt32();if(riff+8L!=input.Length || Encoding.ASCII.GetString(reader.ReadBytes(4))!="WAVE")throw new InvalidOperationException("Malformed RIFF length/type");
        ushort format=0,channels=0,bits=0,align=0;uint rate=0;long data=-1,bytes=0;
        while(input.Position+8<=input.Length)
        {
            var kind=Encoding.ASCII.GetString(reader.ReadBytes(4));var length=reader.ReadUInt32();var start=input.Position;
            if(start+length+(length%2)>input.Length)throw new InvalidOperationException("Wave chunk escapes the file");
            if(kind=="fmt ")
            {
                if(format!=0 || length<16)throw new InvalidOperationException("Duplicate/short wave format");
                format=reader.ReadUInt16();channels=reader.ReadUInt16();rate=reader.ReadUInt32();var byteRate=reader.ReadUInt32();align=reader.ReadUInt16();bits=reader.ReadUInt16();
                if(format!=3 || channels!=2 || rate!=44100 || bits!=32 || align!=8 || byteRate!=rate*align)throw new InvalidOperationException("Expected exact44100Hz stereo float32 waveform");
            }
            else if(kind=="data") {if(data!=-1)throw new InvalidOperationException("Duplicate wave data");data=start;bytes=length;}
            input.Position=start+length+(length%2);
        }
        if(input.Position!=input.Length || format==0 || data<0 || bytes<=0 || bytes%align!=0)throw new InvalidOperationException("Missing/partial waveform samples or chunk header");
        input.Position=data;long samples=bytes/4,clipped=0,silent=0;double peak=0,squares=0;
        for(long i=0;i<samples;i++)
        {
            var value=reader.ReadSingle();if(!float.IsFinite(value))throw new InvalidOperationException("Nonfinite waveform sample");
            var magnitude=Math.Abs((double)value);peak=Math.Max(peak,magnitude);squares+=(double)value*value;if(magnitude>=1)clipped++;if(magnitude<1e-5)silent++;
        }
        var frames=bytes/align;return new((int)rate,channels,frames,frames/(double)rate,peak,Math.Sqrt(squares/samples),clipped/(double)samples,silent/(double)samples);
    }
}
