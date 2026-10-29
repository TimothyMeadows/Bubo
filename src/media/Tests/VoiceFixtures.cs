using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Media.Application;
using Bubo.Media.Domain;
using Bubo.Media.Infrastructure;
using OpenCaw.AssetProduction;
using HostExecutionContext = Bubo.Host.HostExecutionContext;

/// <summary>Owned CPU contract fixtures. No model, GPU, private recording or listening-quality evidence.</summary>
public static class VoiceFixtures
{
    public static void Run(Action<bool,string> check)
    {
        Contracts(check);
        ReferenceAdmission(check);
        NativeWave(check);
        SourceAdaptations(check);
        RuntimeSourceModules(check);
    }

    static VoiceSampling Sampling => new(128,true,.9,50,1,1.05,true,.9,50,1);
    static FileIdentity Identity(string path) => new(path,new string('a',64));
    static VoiceReference Reference => new(Identity("inputs/owned.wav"),Identity("inputs/transcript.txt"),Identity("inputs/rights.json"),Identity("inputs/consent.json"));
    static VoiceOptions Preset => new("english","ryan","Clear neutral delivery",Sampling,null);
    static VoiceOptions Clone => new("english",null,"",Sampling,Reference);
    static OutputBudget Budget => new(1,1048576,1048576,4096,30);

    static void Reject(Action action,Action<bool,string> check,string name)
    {
        try { action(); }
        catch(Exception e) when(e is InvalidOperationException or IOException or JsonException or DecoderFallbackException)
        { check(true,"OWNED CPU voice fixture: "+name);return; }
        throw new InvalidOperationException("Accepted owned voice fixture: "+name);
    }

    static void Contracts(Action<bool,string> check)
    {
        var preset=MediaCapabilities.Require("qwen3-tts-customvoice");
        var clone=MediaCapabilities.Require("qwen3-tts-base");
        check(preset.Modality=="preset-speech" && clone.Modality=="consented-cloning"
            && preset.Id!=clone.Id && preset.WorkerProtocol=="bubo-voice-worker/v1" && clone.WorkerProtocol==preset.WorkerProtocol,
            "voice exposes separate CustomVoice and consented Base capabilities");
        foreach(var language in VoiceOptions.Languages)
        {
            (Preset with{Language=language}).Validate("preset-speech");
            (Clone with{Language=language}).Validate("consented-cloning");
            check(true,"explicit voice language accepted by both profiles: "+language);
        }
        foreach(var speaker in VoiceOptions.Presets)
        {
            (Preset with{Preset=speaker}).Validate("preset-speech");
            check(true,"declared CustomVoice preset accepted: "+speaker);
        }
        foreach(var language in new[]{"auto","English","en","unsupported",""})
            Reject(()=>(Preset with{Language=language}).Validate("preset-speech"),check,"implicit/unsupported language rejected: "+language);
        foreach(var speaker in new[]{"Ryan","invented",""})
            Reject(()=>(Preset with{Preset=speaker}).Validate("preset-speech"),check,"undeclared preset rejected: "+speaker);
        (Preset with{Expression=new string('x',512)}).Validate("preset-speech");
        check(true,"512-character expression remains an explicit supported CustomVoice setting");
        foreach(var expression in new[]{new string('x',513),"owned\0expression"})
            Reject(()=>(Preset with{Expression=expression}).Validate("preset-speech"),check,"unbounded/NUL expression rejected");
        Reject(()=>(Preset with{Expression=null!}).Validate("preset-speech"),check,"null expression cannot become an implicit instruction");
        Reject(()=>(Preset with{Sampling=null!}).Validate("preset-speech"),check,"missing autoregressive settings rejected");
        Reject(()=>(Preset with{Reference=Reference}).Validate("preset-speech"),check,"preset profile cannot silently consume cloning evidence");
        Reject(()=>(Clone with{Preset="ryan"}).Validate("consented-cloning"),check,"cloning cannot silently select a preset");
        Reject(()=>(Clone with{Expression="happy"}).Validate("consented-cloning"),check,"Base cannot silently ignore expression instructions");
        Reject(()=>(Clone with{Reference=null}).Validate("consented-cloning"),check,"cloning requires reference identities");
        Reject(()=>Preset.Validate("voice"),check,"generic modality cannot merge preset and cloning authority");

        foreach(var tokens in new[]{3,2048})
        {
            (Sampling with{MaximumNewTokens=tokens}).Validate();
            check(true,"explicit autoregressive token boundary accepted: "+tokens);
        }
        var invalid=new (string Name,VoiceSampling Value)[]
        {
            ("too few tokens",Sampling with{MaximumNewTokens=2}),
            ("too many tokens",Sampling with{MaximumNewTokens=2049}),
            ("main greedy mode",Sampling with{DoSample=false}),
            ("subtalker greedy mode",Sampling with{SubtalkerDoSample=false}),
            ("main top-k zero",Sampling with{TopK=0}),
            ("main top-k overflow",Sampling with{TopK=201}),
            ("subtalker top-k zero",Sampling with{SubtalkerTopK=0}),
            ("subtalker top-k overflow",Sampling with{SubtalkerTopK=201}),
            ("main temperature zero",Sampling with{Temperature=0}),
            ("main temperature overflow",Sampling with{Temperature=2.01}),
            ("main temperature NaN",Sampling with{Temperature=double.NaN}),
            ("subtalker temperature zero",Sampling with{SubtalkerTemperature=0}),
            ("subtalker temperature overflow",Sampling with{SubtalkerTemperature=2.01}),
            ("subtalker temperature NaN",Sampling with{SubtalkerTemperature=double.NaN}),
            ("subtalker temperature infinity",Sampling with{SubtalkerTemperature=double.PositiveInfinity}),
            ("main top-p zero",Sampling with{TopP=0}),
            ("main top-p overflow",Sampling with{TopP=1.01}),
            ("main top-p NaN",Sampling with{TopP=double.NaN}),
            ("subtalker top-p zero",Sampling with{SubtalkerTopP=0}),
            ("subtalker top-p overflow",Sampling with{SubtalkerTopP=1.01}),
            ("subtalker top-p infinity",Sampling with{SubtalkerTopP=double.NegativeInfinity}),
            ("repetition penalty below range",Sampling with{RepetitionPenalty=.99}),
            ("repetition penalty above range",Sampling with{RepetitionPenalty=2.01}),
            ("repetition penalty NaN",Sampling with{RepetitionPenalty=double.NaN})
        };
        foreach(var (name,value) in invalid)Reject(value.Validate,check,name+" rejected rather than defaulted");
        (Sampling with{TopK=1,SubtalkerTopK=200,Temperature=2,SubtalkerTemperature=2,TopP=1,SubtalkerTopP=1,RepetitionPenalty=2}).Validate();
        check(true,"explicit upper/lower sampling endpoints are supported without defaults");
        var sampling=JsonSerializer.SerializeToNode(Sampling,MediaJson.Options)!.AsObject();
        foreach(var field in sampling.Select(pair=>pair.Key).ToArray())
        {
            var missing=sampling.DeepClone().AsObject();missing.Remove(field);
            Reject(()=>MediaJson.Read<VoiceSampling>(Encoding.UTF8.GetBytes(missing.ToJsonString())),check,"sampling JSON requires explicit "+field);
        }
        var unknown=sampling.DeepClone().AsObject();unknown["seed"]=17;
        Reject(()=>MediaJson.Read<VoiceSampling>(Encoding.UTF8.GetBytes(unknown.ToJsonString())),check,"unknown sampling control cannot be ignored");
        var duplicate=sampling.ToJsonString()[..^1]+",\"temperature\":0.7}";
        Reject(()=>MediaJson.Read<VoiceSampling>(Encoding.UTF8.GetBytes(duplicate)),check,"duplicate autoregressive control rejected");

        var item=new GenerationItem{Id="owned",Prompt="Owned synthetic CPU contract fixture.",Seed=17,Voice=Preset};
        item.Validate("preset-speech",Budget);
        (item with{Voice=Clone}).Validate("consented-cloning",Budget);
        check(true,"both voice profiles accept absent diffusion controls and variable duration");
        var wrong=new (string Name,GenerationItem Value)[]
        {
            ("diffusion steps",item with{Steps=4}), ("diffusion guidance",item with{Guidance=1}),
            ("fixed seconds",item with{Seconds=5}), ("image width",item with{Width=64}),
            ("image height",item with{Height=64}), ("loop candidate",item with{LoopCandidate=true}),
            ("negative prompt",item with{NegativePrompt="noise"}), ("missing voice",item with{Voice=null}),
            ("oversized seed",item with{Seed=(long)int.MaxValue+1}),
            ("oversized UTF8 text",item with{Prompt=new string('\u754c',1500)}),
            ("unrelated music",item with{Music=new("instrumental","","unknown",null,"","",3)})
        };
        foreach(var (name,value) in wrong)Reject(()=>value.Validate("preset-speech",Budget),check,"voice rejects "+name);
        Reject(()=>item.Validate("preset-speech",Budget with{MaximumAudioSeconds=120.01}),check,"voice measured-duration budget remains bounded to120seconds");
        var sfx=new GenerationItem{Id="legacy",Prompt="Owned SFX fixture",Seed=1,Seconds=1};
        Reject(()=>sfx.Validate("sfx",Budget),check,"legacy diffusion still requires real steps and guidance");

        Reference.Validate();
        foreach(var path in new[]{"https://example.invalid/voice.wav","data:audio/wav;base64,AAAA","//server/voice.wav","\\\\server\\voice.wav","C:/voice.wav","/voice.wav","../voice.wav","inputs/../voice.wav","inputs/./voice.wav","inputs//voice.wav","inputs\\voice.wav","inputs/voice.wav\0"})
            Reject(()=>(Reference with{Audio=Identity(path)}).Validate(),check,"noncanonical/remote reference path rejected: "+path.Replace('\0','?'));
        Reject(()=>(Reference with{Transcript=Reference.Audio}).Validate(),check,"audio and transcript cannot alias");
        Reject(()=>(Reference with{Consent=Reference.Rights with{Path="INPUTS/RIGHTS.JSON"}}).Validate(),check,"case aliases cannot collapse consent and rights");
        Reject(()=>(Reference with{Consent=null!}).Validate(),check,"missing consent identity rejected");
        Reject(()=>(Reference with{Rights=Reference.Rights with{Sha256=new string('A',64)}}).Validate(),check,"reference identities require canonical digest spelling");
    }

    static void ReferenceAdmission(Action<bool,string> check)
    {
        var baseline=new AdmissionFixture();var accepted=baseline.Build();
        var admitted=new MediaAdmission(accepted.Workspace).Read("inputs/request.json");
        var identities=new VoiceAdmission(accepted.Workspace).Validate(accepted.Request);
        check(admitted.Capability.Modality=="consented-cloning" && identities.Count==5
            && identities.Contains(accepted.Request.Items[0].Voice!.Reference!.Audio),
            "OWNED CPU admission binds five fresh audio/transcript/rights/consent/source identities; no real consent or voice quality asserted");
        check(!Directory.Exists(Path.Combine(baseline.Root,"state")) && !Directory.Exists(Path.Combine(baseline.Root,"staging")),
            "voice admission creates no execution state, output or acceptance reservation");
        var full=new AdmissionFixture{ConsentScope="local-voice-cloning-qa",AcceptanceCase="consented-cloning"}.Build();
        new VoiceAdmission(full.Workspace).Validate(full.Request);
        check(true,"synthetic QA-only consent is admitted only for its matching cloning acceptance case");
        var probe=new AdmissionFixture{ConsentScope="local-voice-cloning-qa"}.Build();
        new VoiceAdmission(probe.Workspace).Validate(probe.Request,probe:true);
        check(true,"synthetic QA-only consent admits an explicit zero-sample probe");
        RejectCase(new AdmissionFixture{ConsentScope="local-voice-cloning-qa"},"QA-only consent cannot authorize ordinary generation");
        RejectCase(new AdmissionFixture{ConsentScope="local-voice-cloning-qa",AcceptanceCase="preset-speech"},"QA-only consent cannot authorize another acceptance case");
        foreach(var scope in new[]{"public-distribution","all-uses",""})RejectCase(new AdmissionFixture{ConsentScope=scope},"unselected consent scope rejected: "+scope);
        foreach(var path in new[]{"state/voice-fixture/reference.wav","staging/owned/reference.wav"})
            RejectCase(new AdmissionFixture{AudioPath=path},"audio cannot overlap mutable execution state/current output");
        foreach(var path in new[]{"state/voice-fixture/source-consent.txt","staging/owned/source-consent.txt"})
            RejectCase(new AdmissionFixture{SourcePath=path},"consent source evidence cannot overlap mutable execution state/current output");
        RejectCase(new AdmissionFixture{SourceKind="input"},"ordinary input registration cannot confer consent-source authority");
        RejectCase(new AdmissionFixture{OmitRegistration="inputs/source-consent.txt"},"unregistered consent source cannot be trusted");
        foreach(var path in new[]{"inputs/reference.wav","inputs/transcript.txt","inputs/recording-rights.json","inputs/consent.json"})
            RejectCase(new AdmissionFixture{OmitRegistration=path},"missing explicit host reference rejected: "+path);
        RejectCase(new AdmissionFixture{AudioPath="inputs/reference.mp3"},"unselected reference extension rejected before decode");
        RejectCase(new AdmissionFixture{EmptyAudio=true},"empty local reference rejected before worker launch");
        foreach(var bytes in new[]{Array.Empty<byte>(),Encoding.UTF8.GetBytes(" \r\n\t"),Encoding.UTF8.GetBytes("owned\0words"),new byte[]{0xc3,0x28},Encoding.UTF8.GetBytes(new string('x',4097)),new byte[16385]})
            RejectCase(new AdmissionFixture{TranscriptBytes=bytes},"empty/malformed/oversized transcript rejected");
        var rightsCases=new (string Name,Func<VoiceRecordingRights,VoiceRecordingRights> Change)[]
        {
            ("schema",r=>r with{SchemaVersion="unknown"}), ("task",r=>r with{Task="other-task"}),
            ("owner",r=>r with{Owner=" "}), ("rights",r=>r with{RecordingRightsConfirmed=false}),
            ("audio substitution",r=>r with{Audio=r.Audio with{Sha256=new string('b',64)}}),
            ("transcript substitution",r=>r with{Transcript=r.Transcript with{Sha256=new string('b',64)}})
        };
        foreach(var (name,change) in rightsCases)RejectCase(new AdmissionFixture{ChangeRights=change},"fresh registered recording-rights document rejects "+name);
        var consentCases=new (string Name,Func<VoiceConsent,VoiceConsent> Change)[]
        {
            ("schema",c=>c with{SchemaVersion="unknown"}), ("task",c=>c with{Task="other-task"}),
            ("speaker",c=>c with{Speaker=" "}), ("speaker rights",c=>c with{SpeakerRightsConfirmed=false}),
            ("permission",c=>c with{ConsentGranted=false}),
            ("audio substitution",c=>c with{Audio=c.Audio with{Sha256=new string('b',64)}}),
            ("transcript substitution",c=>c with{Transcript=c.Transcript with{Sha256=new string('b',64)}}),
            ("source substitution",c=>c with{SourceEvidence=c.SourceEvidence with{Sha256=new string('b',64)}}),
            ("missing source",c=>c with{SourceEvidence=null!}),
            ("remote source",c=>c with{SourceEvidence=c.SourceEvidence with{Path="https://example.invalid/consent.txt"}}),
            ("source traversal",c=>c with{SourceEvidence=c.SourceEvidence with{Path="inputs/../source-consent.txt"}})
        };
        foreach(var (name,change) in consentCases)RejectCase(new AdmissionFixture{ChangeConsent=change},"fresh registered consent document rejects "+name);
        foreach(var path in new[]{"inputs/reference.wav","inputs/transcript.txt","inputs/recording-rights.json","inputs/consent.json","inputs/source-consent.txt"})
        {
            var fixture=new AdmissionFixture();var current=fixture.Build();
            File.AppendAllText(Path.Combine(fixture.Root,path)," changed after host registration");
            Reject(()=>new VoiceAdmission(current.Workspace).Validate(current.Request),check,"fresh rehash rejects changed "+path);
        }
        foreach(var path in new[]{"inputs/reference.wav","inputs/transcript.txt","inputs/recording-rights.json","inputs/consent.json","inputs/source-consent.txt"})
        {
            var fixture=new AdmissionFixture();var current=fixture.Build();File.Delete(Path.Combine(fixture.Root,path));
            Reject(()=>new VoiceAdmission(current.Workspace).Validate(current.Request),check,"missing registered evidence rejected: "+path);
        }
        void RejectCase(AdmissionFixture fixture,string name)
        {
            var current=fixture.Build();
            Reject(()=>new VoiceAdmission(current.Workspace).Validate(current.Request),check,name);
        }
    }

    static void NativeWave(Action<bool,string> check)
    {
        var root=OwnedRoot("wave");var path=WriteWave(root,"voice.wav",[0,.5f,-1,1.25f]);
        var measured=WaveInspection.Read(path,4096,24000,1);
        check(measured.SampleRate==24000 && measured.Channels==1 && measured.Frames==4
            && measured.DurationSeconds==4/24000d && measured.Peak==1.25 && measured.ClippedFraction==.5 && measured.SilentFraction==.25
            && Math.Abs(measured.Rms-Math.Sqrt(2.8125/4))<1e-12,
            "synthetic mono24000 FLOAT waveform retains every frame and independently measured amplitude; no speech-quality claim");
        Reject(()=>WaveInspection.Read(path,4096),check,"voice mono rate is never reinterpreted as stereo SFX");
        Reject(()=>WaveInspection.Read(path,4096,24000,2),check,"voice is never synthesized into stereo");
        Reject(()=>WaveInspection.Read(WriteWave(root,"wrong-rate.wav",[0,.5f],rate:22050),4096,24000,1),check,"unrequested voice sample rate rejected without resampling");
        Reject(()=>WaveInspection.Read(WriteWave(root,"stereo.wav",[0,.5f],channels:2),4096,24000,1),check,"stereo output rejected rather than silently downmixed");
        Reject(()=>WaveInspection.Read(WriteWave(root,"pcm.wav",[0,.5f],format:1),4096,24000,1),check,"PCM-labelled bytes cannot pass native FLOAT voice output");
        Reject(()=>WaveInspection.Read(WriteWave(root,"nan.wav",[float.NaN]),4096,24000,1),check,"nonfinite mono voice sample rejected");
        Reject(()=>WaveInspection.Read(WriteWave(root,"infinity.wav",[float.PositiveInfinity]),4096,24000,1),check,"infinite mono voice sample rejected");
        Reject(()=>WaveInspection.Read(path,new FileInfo(path).Length-1,24000,1),check,"voice actual byte budget enforced before samples");
    }

    static void SourceAdaptations(Action<bool,string> check)
    {
        var owner=new AdmissionFixture().Build();var host=owner.Workspace.HostContext!;
        var root=Path.Combine(host.Root,"tools","owned-adapted-source");var source=Path.Combine(root,"source");
        Directory.CreateDirectory(Path.Combine(source,".originals"));var archive=Path.Combine(root,"source.zip");
        const string original="# OWNED original import fixture; never executed\n";
        const string replacement="# OWNED audited import adaptation; never executed\n";
        File.WriteAllText(Path.Combine(source,"engine.py"),replacement);
        File.WriteAllText(Path.Combine(source,".originals","engine.py"),original);
        File.WriteAllText(Path.Combine(source,"helper.py"),original);
        using(var stream=File.Create(archive))
        using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))
            foreach(var name in new[]{"engine.py","helper.py"})
            { using var output=zip.CreateEntry("pin/"+name).Open();output.Write(Encoding.UTF8.GetBytes(original)); }
        JsonObject Adaptation()=>new(){["path"]="engine.py",["originalRetainedPath"]=".originals/engine.py",
            ["originalSha256"]=HashText(original),["replacementSha256"]=HashText(replacement),["replacementBytes"]=(long)Encoding.UTF8.GetByteCount(replacement)};
        JsonObject Definition()=>new(){["archiveSha256"]=Workspace.Hash(archive),["archiveBytes"]=new FileInfo(archive).Length,
            ["archiveRootPrefix"]="pin/",["adaptations"]=new JsonArray(Adaptation())};
        VerifiedFiles Observe()=>VerifiedFiles.Observe(host,InstallationVerifier.FilesUnder(host,source).Append(archive));
        void Verify(JsonObject? definition=null)=>EngineSourceIntegrity.Verify(host,source,archive,definition??Definition(),Observe());
        Verify();check(true,"owned replacement and retained original both bind exact pinned archived source bytes");
        File.AppendAllText(Path.Combine(source,"engine.py"),"# tamper");
        Reject(()=>Verify(),check,"self-inventoried replacement mutation cannot change the declared adaptation");
        File.WriteAllText(Path.Combine(source,"engine.py"),replacement);
        File.AppendAllText(Path.Combine(source,".originals","engine.py"),"# tamper");
        Reject(()=>Verify(),check,"retained original mutation cannot replace archived ancestry");
        File.WriteAllText(Path.Combine(source,".originals","engine.py"),original);
        File.Delete(Path.Combine(source,".originals","engine.py"));
        Reject(()=>Verify(),check,"missing original cannot pass an otherwise valid replacement");
        File.WriteAllText(Path.Combine(source,".originals","engine.py"),original);
        File.WriteAllText(Path.Combine(source,"unchecked.py"),original);
        Reject(()=>Verify(),check,"extra importable source outside adaptation closure rejected");File.Delete(Path.Combine(source,"unchecked.py"));
        File.WriteAllText(Path.Combine(source,".originals","unchecked.py"),original);
        Reject(()=>Verify(),check,"original-retention directory cannot smuggle an extra source file");File.Delete(Path.Combine(source,".originals","unchecked.py"));
        foreach(var field in new[]{"originalSha256","replacementSha256"})
        {
            var wrong=Definition();wrong["adaptations"]![0]![field]=new string('b',64);
            Reject(()=>Verify(wrong),check,"adaptation exact "+field+" enforced");
        }
        var length=Definition();length["adaptations"]![0]!["replacementBytes"]=(long)Encoding.UTF8.GetByteCount(replacement)+1;
        Reject(()=>Verify(length),check,"replacement byte count cannot be self-inventoried");
        foreach(var retained in new[]{"originals/engine.py",".originals/../engine.py",".originals\\engine.py",".originals//engine.py"})
        {
            var wrong=Definition();wrong["adaptations"]![0]!["originalRetainedPath"]=retained;
            Reject(()=>Verify(wrong),check,"noncanonical retained-original ancestry rejected: "+retained);
        }
        var unknown=Definition();unknown["adaptations"]!.AsArray().Add(Adaptation());unknown["adaptations"]![1]!["path"]="not-archived.py";
        Reject(()=>Verify(unknown),check,"adaptation cannot declare an unknown archive target");
        var duplicate=Definition();duplicate["adaptations"]!.AsArray().Add(Adaptation());
        Reject(()=>Verify(duplicate),check,"duplicate adaptation targets rejected explicitly");
        var missing=Definition();missing["adaptations"]![0]!.AsObject().Remove("path");
        Reject(()=>Verify(missing),check,"missing adaptation target rejected explicitly");
        foreach(var malformed in new JsonNode?[]{JsonValue.Create("undeclared"),JsonValue.Create(1),null})
        {
            var wrong=Definition();wrong["adaptations"]!.AsArray().Add(malformed);
            Reject(()=>Verify(wrong),check,"nonobject adaptation cannot be silently discarded");
        }
        foreach(var malformed in new JsonNode?[]{new JsonObject(),JsonValue.Create("undeclared"),null})
        {
            var wrong=Definition();wrong["adaptations"]=malformed;
            Reject(()=>Verify(wrong),check,"explicit nonarray adaptation declaration rejected");
        }
        var pinned=Definition();var observed=Observe();File.AppendAllText(archive,"changed after observation");
        Reject(()=>EngineSourceIntegrity.Verify(host,source,archive,pinned,observed),check,"source archive is freshly checked after observation");

        // All seventeen targets below are otherwise valid; this isolates the declaration bound.
        var many=Path.Combine(root,"many");Directory.CreateDirectory(Path.Combine(many,".originals"));
        var manyArchive=Path.Combine(root,"many.zip");var declarations=new JsonArray();
        string Original(int i)=>"# OWNED original "+i+"; never executed\n";
        string Replacement(int i)=>"# OWNED adapted "+i+"; never executed\n";
        JsonObject Declare(int i)=>new(){["path"]=$"unit-{i}.py",["originalRetainedPath"]=$".originals/unit-{i}.py",
            ["originalSha256"]=HashText(Original(i)),["replacementSha256"]=HashText(Replacement(i)),["replacementBytes"]=(long)Encoding.UTF8.GetByteCount(Replacement(i))};
        using(var stream=File.Create(manyArchive))
        using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))
            for(var i=0;i<17;i++)
            {
                using var output=zip.CreateEntry($"pin/unit-{i}.py").Open();output.Write(Encoding.UTF8.GetBytes(Original(i)));
                File.WriteAllText(Path.Combine(many,$"unit-{i}.py"),i<16?Replacement(i):Original(i));
                if(i<16){File.WriteAllText(Path.Combine(many,".originals",$"unit-{i}.py"),Original(i));declarations.Add(Declare(i));}
            }
        void VerifyMany()
        {
            var specification=new JsonObject{["archiveSha256"]=Workspace.Hash(manyArchive),["archiveBytes"]=new FileInfo(manyArchive).Length,
                ["archiveRootPrefix"]="pin/",["adaptations"]=declarations.DeepClone()};
            EngineSourceIntegrity.Verify(host,many,manyArchive,specification,VerifiedFiles.Observe(host,InstallationVerifier.FilesUnder(host,many).Append(manyArchive)));
        }
        VerifyMany();check(true,"sixteen explicit source adaptations preserve all originals at the admitted upper bound");
        File.WriteAllText(Path.Combine(many,"unit-16.py"),Replacement(16));
        File.WriteAllText(Path.Combine(many,".originals","unit-16.py"),Original(16));declarations.Add(Declare(16));
        Reject(VerifyMany,check,"seventeen otherwise valid source adaptations exceed the closed declaration bound");
    }

    static void RuntimeSourceModules(Action<bool,string> check)
    {
        var root=OwnedRoot("runtime-source");var source=Path.Combine(root,"tools","owned-source");
        var paths=new[]{"qwen_tts/__init__.py","qwen_tts/core/__init__.py","qwen_tts/core/models/__init__.py",
            "qwen_tts/core/models/configuration_qwen3_tts.py","qwen_tts/core/models/modeling_qwen3_tts.py",
            "qwen_tts/core/models/processing_qwen3_tts.py","qwen_tts/core/tokenizer_12hz/configuration_qwen3_tts_tokenizer_v2.py",
            "qwen_tts/core/tokenizer_12hz/modeling_qwen3_tts_tokenizer_v2.py","qwen_tts/inference/qwen3_tts_model.py",
            "qwen_tts/inference/qwen3_tts_tokenizer.py"};
        string Module(string relative)=>(relative.EndsWith("/__init__.py",StringComparison.Ordinal)?relative[..^12]:relative[..^3]).Replace('/','.');
        var expected=new Dictionary<string,string>(StringComparer.Ordinal);var inventory=new List<InstalledFile>();var imports=new JsonObject();
        JsonObject AddOwnedFile(string relative,string text)
        {
            var full=Path.Combine(source,relative.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full,text);var hash=Workspace.Hash(full);
            inventory.Add(new(Path.GetRelativePath(root,full).Replace('\\','/'),hash,new FileInfo(full).Length));
            return new(){["path"]=full,["sha256"]=hash};
        }
        foreach(var path in paths)
        {
            var row=AddOwnedFile(path,"# OWNED synthetic runtime module; never imported: "+path+"\n");
            expected.Add(path,row["sha256"]!.GetValue<string>());imports.Add(Module(path),row);
        }
        const string retained=".originals/qwen_tts/core/__init__.py";
        var archived=AddOwnedFile(retained,"# OWNED archived original; inventoried but not an admitted runtime import\n");
        var installation=new PreparedInstallation("qwen3-tts-customvoice","owned-receipt.json",new string('a',64),"owned-worker.py","owned-model",null,
            new Dictionary<string,string>(),new string('b',64),new string('c',64),inventory,SourceDirectory:source,RuntimeSourceFiles:expected);
        FixedMediaWorker.ValidateVoiceSourceModules(installation,imports,root);
        check(true,"ten synthetic runtime imports match the exact audited source table and independent installation inventory");
        JsonObject Rows()=>imports.DeepClone().AsObject();
        var replaced=Rows();replaced.Remove(Module(paths[1]));replaced.Add(Module(retained),archived.DeepClone());
        Reject(()=>FixedMediaWorker.ValidateVoiceSourceModules(installation,replaced,root),check,
            "inventoried retained-original path/hash and matching derived module label cannot replace an audited runtime import");
        var missing=Rows();missing.Remove(Module(paths[0]));
        Reject(()=>FixedMediaWorker.ValidateVoiceSourceModules(installation,missing,root),check,"missing audited runtime import rejected");
        var extra=Rows();extra.Add(Module(retained),archived.DeepClone());
        Reject(()=>FixedMediaWorker.ValidateVoiceSourceModules(installation,extra,root),check,"extra inventoried archive import cannot expand runtime closure");
        var duplicate=Rows();duplicate[Module(paths[1])]=duplicate[Module(paths[0])]!.DeepClone();
        Reject(()=>FixedMediaWorker.ValidateVoiceSourceModules(installation,duplicate,root),check,"duplicate runtime path cannot stand in for another required module");
        var wrongHash=Rows();wrongHash[Module(paths[0])]!["sha256"]=new string('d',64);
        Reject(()=>FixedMediaWorker.ValidateVoiceSourceModules(installation,wrongHash,root),check,"reported runtime digest must equal the audited source digest");
        Reject(()=>FixedMediaWorker.ValidateVoiceSourceModules(installation with{RuntimeSourceFiles=null},imports,root),check,"absent audited runtime source table cannot be replaced by self-report");
        Reject(()=>FixedMediaWorker.ValidateVoiceSourceModules(installation with{SourceDirectory=null},imports,root),check,"runtime imports require their declared immutable source directory");
        Reject(()=>FixedMediaWorker.ValidateVoiceSourceModules(installation with{Files=inventory.Skip(1).ToArray()},imports,root),check,"audited runtime table cannot authorize a file missing from installation inventory");
        var wrongLabel=Rows();var moved=wrongLabel[Module(paths[0])]!.DeepClone();wrongLabel.Remove(Module(paths[0]));wrongLabel.Add("invented.module",moved);
        Reject(()=>FixedMediaWorker.ValidateVoiceSourceModules(installation,wrongLabel,root),check,"runtime module labels must identify their actual audited source paths");
    }

    static string HashText(string text)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    static string OwnedRoot(string name)
    {
        var path=Path.Combine(AppContext.BaseDirectory,"test-fixtures","voice-"+name+"-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);return path;
    }
    static string WriteWave(string root,string relative,float[] samples,int rate=24000,ushort channels=1,ushort format=3)
    {
        var path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream=new FileStream(path,FileMode.Create);using var writer=new BinaryWriter(stream,Encoding.ASCII,true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));writer.Write((uint)(36+samples.Length*4));writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16u);writer.Write(format);writer.Write(channels);writer.Write((uint)rate);writer.Write((uint)(rate*channels*4));writer.Write((ushort)(channels*4));writer.Write((ushort)32);
        writer.Write(Encoding.ASCII.GetBytes("data"));writer.Write((uint)(samples.Length*4));foreach(var value in samples)writer.Write(value);
        return path;
    }

    sealed class AdmissionFixture
    {
        public string Root { get; }=OwnedRoot("admission");
        public string AudioPath { get; init; }="inputs/reference.wav";
        public string SourcePath { get; init; }="inputs/source-consent.txt";
        public string SourceKind { get; init; }="consent";
        public string? OmitRegistration { get; init; }
        public string ConsentScope { get; init; }="local-voice-cloning";
        public string? AcceptanceCase { get; init; }
        public bool EmptyAudio { get; init; }
        public byte[] TranscriptBytes { get; init; }=Encoding.UTF8.GetBytes("Owned synthetic fixture words; no private speech or real transcript.");
        public Func<VoiceRecordingRights,VoiceRecordingRights> ChangeRights { get; init; }=value=>value;
        public Func<VoiceConsent,VoiceConsent> ChangeConsent { get; init; }=value=>value;
        void Bytes(string path,byte[] bytes)
        { var full=Path.Combine(Root,path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);File.WriteAllBytes(full,bytes); }
        void Save(string path,object value)=>Bytes(path,JsonSerializer.SerializeToUtf8Bytes(value,MediaJson.Options));
        FileIdentity BoundFile(string path)=>new(path,Workspace.Hash(Path.Combine(Root,path)));

        public (MediaRequest Request,Workspace Workspace) Build()
        {
            WriteWave(Root,AudioPath,[0,.25f,-.25f]);if(EmptyAudio)Bytes(AudioPath,[]);
            Bytes("inputs/transcript.txt",TranscriptBytes);
            Bytes(SourcePath,Encoding.UTF8.GetBytes("OWNED synthetic consent-source bytes; not evidence of real speaker identity or permission."));
            Save("inputs/media.json",new{purpose="OWNED synthetic media-policy fixture; no real production authority"});
            Save("inputs/install.json",new{purpose="OWNED synthetic receipt; no installation or worker validation"});
            Save("inputs/generation-rights.json",new MediaRights("bubo-media-rights/v1","voice-fixture","owned CPU fixture",true,true));
            Save("inputs/approval.json",new AcceptanceBudgetApproval("bubo-media-acceptance-budget/v1","shared-media",AcceptanceAttempt.Cases,1,1));
            var audio=BoundFile(AudioPath);var transcript=BoundFile("inputs/transcript.txt");
            Save("inputs/recording-rights.json",ChangeRights(new("bubo-voice-reference-rights/v1","voice-fixture","owned synthetic recording",audio,transcript,true)));
            Save("inputs/consent.json",ChangeConsent(new("bubo-voice-consent/v1","voice-fixture","synthetic speaker assertion",audio,transcript,true,true,ConsentScope,BoundFile(SourcePath))));
            var reference=new VoiceReference(audio,transcript,BoundFile("inputs/recording-rights.json"),BoundFile("inputs/consent.json"));
            var request=new MediaRequest{SchemaVersion="bubo-media-request/v1",RequestId="owned",Backend="qwen3-tts-base",Task="voice-fixture",OutputDirectory="staging",
                Rights=BoundFile("inputs/generation-rights.json"),Installation=BoundFile("inputs/install.json"),Settings=new("cuda:0","bfloat16","none"),Budget=Budget,
                Items=[new(){Id="owned",Prompt="Owned CPU fixture, not a generation request.",Seed=17,Voice=Clone with{Reference=reference}}],
                Acceptance=AcceptanceCase is null?null:new(AcceptanceCase,"candidate",BoundFile("inputs/approval.json"))};
            Save("inputs/request.json",request);
            var references=new JsonArray();
            foreach(var (path,kind) in new[]{("inputs/media.json","policy"),("inputs/install.json","input"),("inputs/generation-rights.json","rights"),("inputs/approval.json","policy"),
                (AudioPath,"input"),("inputs/transcript.txt","input"),("inputs/recording-rights.json","rights"),("inputs/consent.json","consent"),(SourcePath,SourceKind),("inputs/request.json","input")})
                if(path!=OmitRegistration)references.Add(new JsonObject{["path"]=path,["sha256"]=BoundFile(path).Sha256,["kind"]=kind});
            var context=new JsonObject{["schemaVersion"]="bubo-host-context/v1",["projectRoot"]=Root,["task"]="voice-fixture",
                ["roots"]=new JsonObject{["state"]="state/voice-fixture",["staging"]="staging",["mediaState"]="shared-media",["tools"]=new JsonArray("tools"),
                    ["inputs"]=new JsonArray("inputs"),["outputs"]=new JsonArray("runtime")},
                ["policy"]=new JsonObject{["purpose"]="local-media",["pipeline"]="LOCAL",["media"]=new JsonObject{["path"]="inputs/media.json",["sha256"]=BoundFile("inputs/media.json").Sha256}},
                ["references"]=references,["tools"]=new JsonArray(),["budgets"]=new JsonObject{["timeoutSeconds"]=30,["maxLogBytes"]=4096}};
            Bytes("context.json",Encoding.UTF8.GetBytes(context.ToJsonString()));
            return(request,new Workspace(Root,HostExecutionContext.Load(Root,"context.json")));
        }
    }
}
