using System.Text;
using System.Text.Json.Serialization;
using Bubo.Media.Domain;
using OpenCaw.AssetProduction;

namespace Bubo.Media.Application;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VoiceRecordingRights(string SchemaVersion,string Task,string Owner,FileIdentity Audio,FileIdentity Transcript,bool RecordingRightsConfirmed);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VoiceConsent(string SchemaVersion,string Task,string Speaker,FileIdentity Audio,FileIdentity Transcript,
    bool SpeakerRightsConfirmed,bool ConsentGranted,string Scope,FileIdentity SourceEvidence);

/// <summary>Host assertions bound to exact private source bytes; never infers consent or speaker identity.</summary>
public sealed class VoiceAdmission(IWorkspace workspace)
{
    public IReadOnlyList<FileIdentity> Validate(MediaRequest request,bool probe=false)
    {
        var files=new List<FileIdentity>();
        if(request.Backend!="qwen3-tts-base")return files;
        var admission=new MediaAdmission(workspace);var host=admission.Context;
        foreach(var item in request.Items)
        {
            var reference=item.Voice?.Reference??throw new InvalidOperationException("Base cloning requires explicit reference-bound evidence");
            reference.Validate();
            foreach(var (identity,kind) in new[]{(reference.Audio,"input"),(reference.Transcript,"input"),(reference.Rights,"rights"),(reference.Consent,"consent")})
            {
                admission.Reference(identity,kind);files.Add(identity);
                var full=host.ResolveReadPath(identity.Path);
                if(MediaAdmission.Within(full,host.ResolveWritePath(host.StateRoot))
                    || MediaAdmission.Within(full,host.ResolveWritePath(request.OutputDirectory.TrimEnd('/')+"/"+request.RequestId)))
                    throw new InvalidOperationException("Cloning evidence cannot overlap mutable execution state or this output");
            }
            var audio=host.ResolveReadPath(reference.Audio.Path);
            if(new FileInfo(audio).Length is <1 or >20971520 || Path.GetExtension(audio).ToLowerInvariant() is not (".m4a" or ".wav"))
                throw new InvalidOperationException("Voice reference requires a bounded local M4A/AAC or WAV file");
            using var transcriptInput=new FileStream(host.ResolveReadPath(reference.Transcript.Path),FileMode.Open,FileAccess.Read,FileShare.Read);
            if(transcriptInput.Length is <1 or >16384)throw new InvalidOperationException("Reference transcript must contain1byte..16KiB");
            var transcriptBytes=new byte[checked((int)transcriptInput.Length)];transcriptInput.ReadExactly(transcriptBytes);
            var transcript=new UTF8Encoding(false,true).GetString(transcriptBytes);
            if(string.IsNullOrWhiteSpace(transcript)||transcript.Contains('\0')||transcript.Length>4096)throw new InvalidOperationException("Exact nonempty UTF8 reference transcript is required within4096characters");
            var rights=MediaJson.Read<VoiceRecordingRights>(MediaAdmission.ReadBounded(host.ResolveReadPath(reference.Rights.Path)));
            var consent=MediaJson.Read<VoiceConsent>(MediaAdmission.ReadBounded(host.ResolveReadPath(reference.Consent.Path)));
            if(rights.SchemaVersion!="bubo-voice-reference-rights/v1"||rights.Task!=request.Task||!Owner(rights.Owner)||!rights.RecordingRightsConfirmed
                ||rights.Audio!=reference.Audio||rights.Transcript!=reference.Transcript)
                throw new InvalidOperationException("Current recording rights must bind the exact local audio and transcript");
            if(consent.SchemaVersion!="bubo-voice-consent/v1"||consent.Task!=request.Task||!Owner(consent.Speaker)||!consent.SpeakerRightsConfirmed||!consent.ConsentGranted
                ||consent.Audio!=reference.Audio||consent.Transcript!=reference.Transcript||consent.SourceEvidence is null
                ||consent.Scope is not ("local-voice-cloning" or "local-voice-cloning-qa"))
                throw new InvalidOperationException("Explicit current speaker consent must bind this exact recording, transcript and local cloning scope");
            if(consent.Scope=="local-voice-cloning-qa" && !probe && request.Acceptance?.Case!="consented-cloning")
                throw new InvalidOperationException("QA-only voice consent cannot authorize an unrelated generation request");
            VoiceReference.ValidateLocal(consent.SourceEvidence);admission.Reference(consent.SourceEvidence,"consent");
            var sourceEvidence=host.ResolveReadPath(consent.SourceEvidence.Path);
            if(MediaAdmission.Within(sourceEvidence,host.ResolveWritePath(host.StateRoot))
                ||MediaAdmission.Within(sourceEvidence,host.ResolveWritePath(request.OutputDirectory.TrimEnd('/')+"/"+request.RequestId)))
                throw new InvalidOperationException("Source consent evidence cannot overlap mutable execution state or this output");
            files.Add(consent.SourceEvidence);
        }
        return files.Distinct().ToArray();
    }
    static bool Owner(string value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=160&&!value.Contains('\0');
}
