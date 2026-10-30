using System.Text.Json.Serialization;

namespace Bubo.Media.Domain;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VoiceOptions(string Language,string? Preset,string Expression,VoiceSampling Sampling,VoiceReference? Reference)
{
    public static readonly string[] Languages=["chinese","english","french","german","italian","japanese","korean","portuguese","russian","spanish"];
    public static readonly string[] Presets=["aiden","dylan","eric","ono_anna","ryan","serena","sohee","uncle_fu","vivian"];
    public void Validate(string modality)
    {
        if(!Languages.Contains(Language,StringComparer.Ordinal) || Expression is null || Expression.Length>512 || Expression.Contains('\0') || Sampling is null)
            throw new InvalidOperationException("Voice requires an explicit supported language, bounded expression and autoregressive sampling settings");
        Sampling.Validate();
        if(modality=="preset-speech")
        {
            if(!Presets.Contains(Preset,StringComparer.Ordinal) || Reference is not null)
                throw new InvalidOperationException("CustomVoice requires one declared supported preset and no cloning reference");
        }
        else if(modality=="consented-cloning")
        {
            if(Preset is not null || Expression.Length!=0 || Reference is null)
                throw new InvalidOperationException("Base cloning requires local reference evidence; preset and expression instructions are unsupported");
            Reference.Validate();
        }
        else throw new InvalidOperationException("Unsupported voice model modality");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VoiceSampling(
    [property:JsonRequired]int MaximumNewTokens,[property:JsonRequired]bool DoSample,
    [property:JsonRequired]double Temperature,[property:JsonRequired]int TopK,[property:JsonRequired]double TopP,
    [property:JsonRequired]double RepetitionPenalty,[property:JsonRequired]bool SubtalkerDoSample,
    [property:JsonRequired]double SubtalkerTemperature,[property:JsonRequired]int SubtalkerTopK,[property:JsonRequired]double SubtalkerTopP)
{
    public void Validate()
    {
        if(MaximumNewTokens is <3 or >2048 || !DoSample || !SubtalkerDoSample || TopK is <1 or >200 || SubtalkerTopK is <1 or >200
            || !Positive(Temperature,2) || !Positive(SubtalkerTemperature,2) || !Positive(TopP,1) || !Positive(SubtalkerTopP,1)
            || !double.IsFinite(RepetitionPenalty) || RepetitionPenalty is <1 or >2)
            throw new InvalidOperationException("Unsupported bounded Qwen autoregressive sampling settings; no implicit generation defaults");
    }
    static bool Positive(double value,double maximum)=>double.IsFinite(value)&&value>0&&value<=maximum;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VoiceReference(FileIdentity Audio,FileIdentity Transcript,FileIdentity Rights,FileIdentity Consent)
{
    public FileIdentity[] Files()=>[Audio,Transcript,Rights,Consent];
    public void Validate()
    {
        if(Audio is null || Transcript is null || Rights is null || Consent is null)
            throw new InvalidOperationException("Cloning requires local audio, transcript, recording-rights and consent identities");
        foreach(var file in Files())
            ValidateLocal(file);
        if(Files().Select(file=>file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=4)
            throw new InvalidOperationException("Audio, transcript, rights and consent require distinct file identities");
    }
    public static void ValidateLocal(FileIdentity file)
    {
        file.Validate();
        if(file.Path.Contains(':') || file.Path.Contains('\\') || file.Path.Contains('\0') || file.Path.StartsWith('/')
            || file.Path.Split('/').Any(part=>part is "" or "." or ".."))
            throw new InvalidOperationException("Voice references require canonical local project paths; URL/data/remote/escaping forms are rejected");
    }
}
