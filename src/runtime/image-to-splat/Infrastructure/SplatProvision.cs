using System.Text.Json.Nodes;
using Bubo.Splats.Domain;
using Hephaera.AssetProduction;

namespace Bubo.Splats.Infrastructure;

public sealed record SplatToolIdentity {public required string Path {get;init;} public required string Sha256 {get;init;}}
public sealed record SplatProvisionRequest
{
    public required string SchemaVersion {get;init;}
    public required string Task {get;init;}
    public required string InstallationRoot {get;init;}
    public required string DownloadRoot {get;init;}
    public required SplatToolIdentity BootstrapPython {get;init;}
    public required string RuntimeSourceRoot {get;init;}
    public required string NoticeSourceRoot {get;init;}
    public required SplatSource[] Licenses {get;init;}
}
/// <summary>Separate explicit setup; downloads only the exact audited closure. Production never invokes this service.</summary>
public sealed class SplatProvision(Workspace workspace,AuditedResources resources,INativeProcess process)
{
    public async Task<object> Execute(string path,bool execute,CancellationToken cancellation)
    {
        var host=workspace.HostContext??throw new InvalidOperationException("Explicit host context required");host.ValidateFresh();
        if(host.InspectionOnly || host.MediaOnly || host.Pipeline!="LOCAL" || host.AllowPromotion)throw new InvalidOperationException("Splat setup requires full-art LOCAL without promotion authority");
        var request=SplatJson.Read<SplatProvisionRequest>(workspace,path);
        if(request.SchemaVersion!="bubo-splat-provision/v1" || request.Task!=host.TaskId || request.BootstrapPython is null
            || request.Licenses is not {Length:>0 and <=16})throw new InvalidOperationException("Complete scoped explicit provisioning request required");
        foreach(var identity in request.Licenses)SplatRules.ValidateSource(identity);
        SplatRules.ValidateHash(request.BootstrapPython.Sha256);
        var install=ToolRoot(request.InstallationRoot,true);var download=ToolRoot(request.DownloadRoot,true);
        var runtime=ToolRoot(request.RuntimeSourceRoot,false);var notices=ToolRoot(request.NoticeSourceRoot,false);
        if(Overlap(install,download) || new[]{runtime,notices,workspace.Resolve(path)}.Any(x=>Overlap(x,install) || Overlap(x,download))
            || new[]{install,download}.Any(x=>Overlap(x,workspace.Resolve(host.StateRoot)) || Overlap(x,workspace.Resolve(host.StagingRoot))))
            throw new InvalidOperationException("Setup install/download roots must be disjoint from existing inputs, state and staging");
        var hostDocument=SplatJson.ReadFile<JsonObject>(Path.Combine(host.Root,host.ContextPath));
        foreach(var node in (hostDocument["roots"]!["outputs"] as JsonArray??new JsonArray()))
            if(new[]{install,download}.Any(x=>Overlap(x,host.ResolveWritePath(node!.GetValue<string>()))))
                throw new InvalidOperationException("Setup must not overlap declared runtime output roots");
        var catalogPath=resources.Resolve("resources/image-to-splat/catalog.json");var catalog=SplatJson.ReadFile<JsonObject>(catalogPath);
        var candidate=catalog["environmentCandidates"]!["win-amd64"]!.AsObject();
        var files=catalog["weights"]!.AsArray().Concat(catalog["source"]!["files"]!.AsArray()).Select(x=>x!.DeepClone()).ToArray();
        var total=catalog["weightBytes"]!.GetValue<long>()+candidate["artifacts"]!.AsArray().Sum(x=>x!["size"]!.GetValue<long>())
            +candidate["python"]!["bytes"]!.GetValue<long>()+catalog["source"]!["files"]!.AsArray().Sum(x=>x!["bytes"]!.GetValue<long>());
        var plan=new {schemaVersion="bubo-splat-provision-plan/v1",mode=execute?"explicit-execute":"dry-run",backend="triposplat",
            request.InstallationRoot,request.DownloadRoot,request.RuntimeSourceRoot,request.NoticeSourceRoot,
            catalogSha256=Workspace.Hash(catalogPath),downloadBytes=total,minimumFreeDiskBytes=25769803776L,
            maximumInstalledBytes=42949672960L,files,environment=candidate.DeepClone(),notices=catalog["notices"]!.DeepClone(),
            platform="win-amd64",platformSupported=OperatingSystem.IsWindows() && Environment.Is64BitProcess,
            installation=execute,networkAccess=execute,generation=false,gpuUsed=false,licenseAcceptance=false,globalSoftwareChanges=false,
            requirements=new[]{"separate explicit local installation approval","current applicable component licence records including DINOv3","existing project-local pinned CRT and notices","fresh installation root; preserved failures/partials","separate component-review registration and generation approval"}};
        if(!execute)return plan;
        if(!host.AllowProvisioning)throw new InvalidOperationException("Explicit provisioning permission required; nothing installed");
        if(!OperatingSystem.IsWindows() || !Environment.Is64BitProcess || !process.SupportsPrivateMemoryLimit || host.MaxPrivateBytes is null)
            throw new PlatformNotSupportedException("Explicit bounded setup initially requires64-bit Windows owned jobs; no fallback");
        var accepted=new HashSet<string>(StringComparer.Ordinal);
        foreach(var identity in request.Licenses)
        {
            var licence=SplatJson.Read<SplatLicenseEvidence>(workspace,identity.Path,"rights");
            if(workspace.HashProjectFile(identity.Path)!=identity.Sha256 || licence.SchemaVersion!="bubo-splat-license-evidence/v1"
                || licence.Task!=host.TaskId || licence.Backend!="triposplat" || !licence.Accepted || licence.LicenseIds is not {Length:>0 and <=64}
                || licence.Evidence is not {Length:>0 and <=64})throw new InvalidOperationException("Exact existing applicable licence evidence required before setup");
            foreach(var evidence in licence.Evidence)
            { SplatRules.ValidateSource(evidence);host.RequireReference(evidence.Path,"rights");if(workspace.HashProjectFile(evidence.Path)!=evidence.Sha256)throw new InvalidOperationException("Licence evidence changed"); }
            foreach(var id in licence.LicenseIds)accepted.Add(id);
        }
        if(catalog["licenseComponents"]!.AsArray().Any(x=>!accepted.Contains(x!["id"]!.GetValue<string>())))
            throw new InvalidOperationException("A selected component licence has not been approved");
        var python=Path.GetFullPath(request.BootstrapPython.Path,host.Root);host.EnsureToolAllowed(python);
        if(host.HashToolFile(python)!=request.BootstrapPython.Sha256)throw new InvalidOperationException("Bootstrap Python changed");
        if(Directory.Exists(install) || File.Exists(install))throw new InvalidOperationException("Installation path already exists; reconcile and preserve it");
        var helper=resources.Resolve("resources/media/provision.py");var script=resources.Resolve("resources/image-to-splat/provision.py");
        var referenced=catalog["notices"]!.AsArray().Select(x=>resources.Resolve(x!["resource"]!.GetValue<string>())).Concat(new[]{helper,catalogPath,script}).Distinct().ToArray();
        var state=host.StateRoot+"/image-to-splat/provision/"+Guid.NewGuid().ToString("N");var job=state+"/job.json";var result=state+"/result.json";
        SplatJson.WriteNew(workspace,job,new {schemaVersion="bubo-splat-provision-worker/v1",projectRoot=host.Root,installRoot=install,downloadRoot=download,
            catalog=catalogPath,catalogSha256=Workspace.Hash(catalogPath),helper,helperSha256=Workspace.Hash(helper),bootstrapPython=new{path=python,sha256=request.BootstrapPython.Sha256},
            runtimeSourceRoot=runtime,noticeSourceRoot=notices,resourceIdentities=referenced.Select(x=>new{path=x,sha256=Workspace.Hash(x)}).ToArray(),
            licenses=request.Licenses.Select(x=>new{path=workspace.Resolve(x.Path),x.Sha256}).ToArray()});
        await process.RunWithLimits(python,["-I","-B","-X","utf8",script,workspace.Resolve(job),workspace.ResolveWrite(result),workspace.HashProjectFile(job)],
            host.Root,workspace.ResolveWrite(state+"/setup.log"),host.TimeoutSeconds,host.MaxPrivateBytes.Value,cancellation,host.MaxLogBytes,
            new Dictionary<string,string>{["PYTHONDONTWRITEBYTECODE"]="1",["PIP_DISABLE_PIP_VERSION_CHECK"]="1",["PIP_CONFIG_FILE"]="NUL"});
        host.ValidateFresh();var outcome=SplatJson.ReadFile<JsonObject>(workspace.Resolve(result),1048576);
        if(outcome["schemaVersion"]?.GetValue<string>()!="bubo-splat-provision-worker-result/v1" || outcome["status"]?.GetValue<string>()!="installed-pending-registration")
            throw new InvalidOperationException("Setup worker did not produce the expected immutable result");
        return new {schemaVersion="bubo-splat-provision-result/v1",status="installed-pending-registration",plan,result=workspace.Relative(result),data=outcome,
            nativeReadinessVerified=false,generation=false,promotion=false};
    }
    string ToolRoot(string path,bool write)
    {
        SplatRules.ValidateSource(new(){Path=path,Sha256=new string('0',64)});
        var host=workspace.HostContext!;var full=write?host.ResolveWritePath(path):host.ResolveReadPath(path);
        if(!host.ToolRoots.Any(x=>Within(full,host.ResolveReadPath(x))))throw new InvalidOperationException("Setup roots must be explicit project-local tool roots");
        return full;
    }
    static bool Within(string path,string root)=>path.Equals(root,StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    static bool Overlap(string a,string b)=>Within(a,b) || Within(b,a);
}
