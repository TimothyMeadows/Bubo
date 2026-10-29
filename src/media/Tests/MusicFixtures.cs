using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text;
using System.Text.Json;
using Bubo.Media.Application;
using Bubo.Media.Domain;
using Bubo.Media.Infrastructure;
using OpenCaw.AssetProduction;
using HostExecutionContext=Bubo.Host.HostExecutionContext;

static class MusicFixtures
{
    public static void Run(HostExecutionContext host,Action<bool,string> check)
    {
        void Denied(Action action,string name)
        {
            try{action();}catch(Exception e) when(e is InvalidOperationException or IOException or JsonException){check(true,name);return;}
            throw new InvalidOperationException("Accepted "+name);
        }
        var root=Path.Combine(host.Root,"tools","owned-source-fixture");Directory.CreateDirectory(root);
        var source=Path.Combine(root,"source");Directory.CreateDirectory(source);var code=Path.Combine(source,"engine.py");File.WriteAllText(code,"# OWNED fixture; never executed\n");
        var archive=Path.Combine(root,"source.zip");
        void Archive(string name="pin/engine.py")
        {
            using var stream=new FileStream(archive,FileMode.Create);using var zip=new ZipArchive(stream,ZipArchiveMode.Create);
            using var writer=new StreamWriter(zip.CreateEntry(name).Open());writer.Write("# OWNED fixture; never executed\n");
        }
        JsonObject Definition()=>new(){["archiveSha256"]=Workspace.Hash(archive),["archiveBytes"]=new FileInfo(archive).Length,["archiveRootPrefix"]="pin/"};
        VerifiedFiles Observe()=>VerifiedFiles.Observe(host,InstallationVerifier.FilesUnder(host,source).Append(archive));
        Archive();var pin=Definition();EngineSourceIntegrity.Verify(host,source,archive,pin,Observe());check(true,"actual closed source tree matches decompressed pinned ZIP bytes");
        File.AppendAllText(code,"# modified");Denied(()=>EngineSourceIntegrity.Verify(host,source,archive,pin,Observe()),"self-inventoried source mutation cannot pass pinned archive verification");
        File.WriteAllText(code,"# OWNED fixture; never executed\n");var extra=Path.Combine(source,"unchecked.py");File.WriteAllText(extra,"# not in archive");
        Denied(()=>EngineSourceIntegrity.Verify(host,source,archive,pin,Observe()),"extra importable source cannot enter the closed engine tree");File.Delete(extra);
        var before=Observe();File.AppendAllText(archive,"modified after observation");Denied(()=>EngineSourceIntegrity.Verify(host,source,archive,pin,before),"source archive is freshly rehashed before decompression");
        Archive("pin/../escaped.py");Denied(()=>EngineSourceIntegrity.Verify(host,source,archive,Definition(),Observe()),"source ZIP traversal rejected before source resolution");
        Archive("pin/alias\\engine.py");Denied(()=>EngineSourceIntegrity.Verify(host,source,archive,Definition(),Observe()),"source ZIP backslash aliases rejected");
        var installed=new Bubo.Media.Application.InstalledFile("tools/owned-source-fixture/source/engine.py",Workspace.Hash(code),new FileInfo(code).Length);
        InstallationVerifier.RequireInventoryNamespace(host,[installed],[source],[]);check(true,"verified source namespace admits its already archive-checked file");
        var rogue=installed with{Path="tools/rogue/plugin.dll"};Denied(()=>InstallationVerifier.RequireInventoryNamespace(host,[installed,rogue],[source],[]),"self-inventoried DLL outside backend roots cannot expand module trust");
        rogue=rogue with{Path="tools/notices/plugin.dll"};var notice=Path.Combine(host.Root,"tools/notices/VS-Redist.txt");
        Denied(()=>InstallationVerifier.RequireInventoryNamespace(host,[rogue],[source],[notice]),"notice directory cannot confer executable trust beside a pinned notice");
        InstallationVerifier.RequireInventoryNamespace(host,[installed with{Path="tools/notices/VS-Redist.txt"}],[source],[notice]);check(true,"only exact pinned notice path enters the installation namespace");
        var item=new GenerationItem{Id="owned-music",Prompt="Owned fixture caption",Seed=17,Steps=8,Guidance=1,Seconds=20,Music=new("supplied-lyrics","Owned words","en",100,"C major","4",3)};
        var value=JsonNode.Parse("""
        {"protocol":"bubo-music-settings/v1","sourceRevision":"ca1e85fe9430179831e6bc6be790c332190a3866","mode":"supplied-lyrics","planningLmLoaded":false,"lmMetadata":null,"innerSeed":17,"normalization":false,"cpuDecode":false,"attention":"sdpa","actualOutputCount":1,"sampleRate":48000,
        "conditioning":[{"maximumTokens":256,"tokenCount":30,"truncated":false,"text":"owned caption conditioning"},{"maximumTokens":2048,"tokenCount":5,"truncated":false,"text":"owned words"}],
        "params":{"caption":"Owned fixture caption","lyrics":"Owned words","instrumental":false,"vocal_language":"en","duration":20,"seed":17,"inference_steps":8,"guidance_scale":1,"shift":3,"keyscale":"C major","timesignature":"4","bpm":100,"task_type":"text2music","infer_method":"ode","sampler_mode":"euler","thinking":false,"use_cot_metas":false,"use_cot_caption":false,"use_cot_language":false,"use_cot_lyrics":false,"enable_normalization":false,"dcw_enabled":false,"fade_in_duration":0,"fade_out_duration":0,"latent_shift":0,"latent_rescale":1,"reference_audio":null,"src_audio":null,"audio_codes":""},
        "config":{"batch_size":1,"allow_lm_batch":false,"use_random_seed":false,"seeds":[17]}}
        """)!.AsObject();
        FixedMediaWorker.ValidateMusicMetadata(item,value);check(true,"SIMULATED music metadata binds original lyrics, inner seed and disabled planning/postprocessing");
        foreach(var field in new[]{"thinking","use_cot_caption","use_cot_language","use_cot_lyrics","enable_normalization","dcw_enabled"})
        {
            var altered=value.DeepClone().AsObject();altered["params"]![field]=true;Denied(()=>FixedMediaWorker.ValidateMusicMetadata(item,altered),"SIMULATED actual forbidden "+field+" rejected");
        }
        var changed=value.DeepClone().AsObject();changed["innerSeed"]=18;Denied(()=>FixedMediaWorker.ValidateMusicMetadata(item,changed),"SIMULATED inner seed mismatch rejected despite matching outer seed");
        changed=value.DeepClone().AsObject();changed["params"]!["lyrics"]="invented words";Denied(()=>FixedMediaWorker.ValidateMusicMetadata(item,changed),"SIMULATED rewritten lyrics rejected");
        changed=value.DeepClone().AsObject();changed["conditioning"]![1]!["truncated"]=true;Denied(()=>FixedMediaWorker.ValidateMusicMetadata(item,changed),"SIMULATED truncated lyric evidence rejected");
        var cleanup=MediaJson.Read<WorkerCleanup>(Encoding.UTF8.GetBytes("""{"mechanism":"torch._C._cuda_clearCublasWorkspaces","allocatedBeforeWorkspaceClearBytes":8519680,"reservedBeforeWorkspaceClearBytes":20971520,"endingAllocatedBytes":0,"endingReservedBytes":0}"""));
        check(cleanup.AllocatedBeforeWorkspaceClearBytes==8519680 && cleanup.EndingAllocatedBytes==0,"SIMULATED cleanup JSON preserves explicit actual counters");
        Denied(()=>MediaJson.Read<WorkerCleanup>(Encoding.UTF8.GetBytes("""{"mechanism":"torch._C._cuda_clearCublasWorkspaces"}""")),"missing cleanup counters cannot masquerade as observed zero allocation");
    }
}
