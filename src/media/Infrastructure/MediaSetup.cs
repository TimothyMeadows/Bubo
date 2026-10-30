using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Media.Application;
using Bubo.Media.Domain;
using OpenCaw.AssetProduction;

namespace Bubo.Media.Infrastructure;

/// <summary>Explicitly plans and registers an installed, offline dependency set. Never invoked by generation.</summary>
public sealed class MediaSetup(IWorkspace workspace,AuditedResources resources)
{
    public sealed record PythonIdentity(string Path,string Sha256,string Version);
    public sealed record NativeIdentity(string Path,string Sha256,string WorkerSourceSha256,string WorkerBuildSha256,string Revision,string GgmlRevision,string CutlassRevision);
    public sealed record SetupRequest(string SchemaVersion,string Task,string Backend,string ReceiptPath,string ModelDirectory,FileIdentity[] Licenses,
        string? EnvironmentRoot=null,string? PythonBaseRoot=null,PythonIdentity? Python=null,string? NativeRuntimeRoot=null,NativeIdentity? NativeWorker=null,string? WheelhouseRoot=null,string? PythonArchive=null,string? NoticeDirectory=null,
        string? SourceRoot=null,string? SourceArchive=null);

    public Task<object> Execute(string requestPath,bool execute,CancellationToken cancellation)
    {
        var host=workspace.HostContext??throw new InvalidOperationException("Explicit host context required");
        host.ValidateFresh();host.RequireReference(requestPath,"input");
        var request=MediaJson.Read<SetupRequest>(MediaAdmission.ReadBounded(host.ResolveReadPath(requestPath)));
        var capability=MediaCapabilities.Require(request.Backend);
        if(request.SchemaVersion!="bubo-media-setup/v1" || request.Task!=host.TaskId || request.Licenses is not {Length:>0 and <=16})
            throw new InvalidOperationException("Setup requires matching schema/task and explicit license evidence");
        foreach(var identity in request.Licenses)
        {
            identity.Validate();host.RequireReference(identity.Path,"rights");
            if(workspace.HashProjectFile(identity.Path)!=identity.Sha256)throw new InvalidOperationException("Setup license evidence changed");
        }
        var model=host.ResolveReadPath(request.ModelDirectory);
        var destination=host.ResolveWritePath(request.ReceiptPath);
        if(File.Exists(destination) || Directory.Exists(destination))throw new InvalidOperationException("Installation receipt already exists; use a new explicit receipt identity");
        var catalogPath=resources.Resolve("resources/media/catalog.json");
        var catalog=JsonNode.Parse(File.ReadAllBytes(catalogPath))!.AsObject();
        var definition=catalog["backends"]!.AsArray().Single(x=>x!["id"]!.GetValue<string>()==request.Backend)!.AsObject();
        var roots=new List<string>{model};
        if(request.Backend=="flux-schnell")
        {
            if(request.NativeWorker is null || request.NativeRuntimeRoot is null || request.Python is not null || request.EnvironmentRoot is not null || request.PythonBaseRoot is not null || request.WheelhouseRoot is not null || request.PythonArchive is not null || request.SourceRoot is not null || request.SourceArchive is not null)
                throw new InvalidOperationException("Image setup requires an explicit native bundle; Python settings do not apply");
            var bundle=host.ResolveReadPath(request.NativeRuntimeRoot);roots.Add(bundle);
            var native=Path.GetFullPath(request.NativeWorker.Path,host.Root);host.EnsureToolAllowed(native);
            if(!MediaAdmission.Within(native,bundle))throw new InvalidOperationException("Native worker must be inside its complete runtime bundle");
        }
        else if(request.Backend is "stable-audio-open" or "ace-step-turbo" or "qwen3-tts-customvoice" or "qwen3-tts-base")
        {
            if(request.Python is null || request.EnvironmentRoot is null || request.PythonBaseRoot is null || request.WheelhouseRoot is null || request.PythonArchive is null || request.NativeWorker is not null || request.NativeRuntimeRoot is not null)
                throw new InvalidOperationException("Python media setup requires its dedicated environment and pinned base Python runtime");
            roots.Add(host.ResolveReadPath(request.EnvironmentRoot));roots.Add(host.ResolveReadPath(request.PythonBaseRoot));
            roots.Add(host.ResolveReadPath(request.WheelhouseRoot));
            var python=Path.GetFullPath(request.Python.Path,host.Root);host.EnsureToolAllowed(python);
            if(!MediaAdmission.Within(python,roots[1]))throw new InvalidOperationException("Python executable must belong to its dedicated environment");
            if(request.Backend is "ace-step-turbo" or "qwen3-tts-customvoice" or "qwen3-tts-base")
            {
                if(request.SourceRoot is null || request.SourceArchive is null)throw new InvalidOperationException("Selected backend requires its pinned closed source tree/archive");
                roots.Add(host.ResolveReadPath(request.SourceRoot));
            }
            else if(request.SourceRoot is not null || request.SourceArchive is not null)throw new InvalidOperationException("SFX cannot select an arbitrary source package");
        }
        else throw new InvalidOperationException("Unknown setup backend; no native/Python fallback");
        if(request.NoticeDirectory is {} notices)roots.Add(host.ResolveReadPath(notices));
        foreach(var root in roots)
            if(MediaAdmission.Within(destination,root))throw new InvalidOperationException("Installation receipt must be outside immutable dependency roots");
        if(roots.Distinct(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal).Count()!=roots.Count
            || roots.Any(a=>roots.Any(b=>a!=b && MediaAdmission.Within(a,b))))throw new InvalidOperationException("Dependency roots must be disjoint");
        var plan=new {schemaVersion="bubo-media-setup-plan/v1",backend=request.Backend,capability.Modality,catalogSha256=Workspace.Hash(catalogPath),
            action="verify-and-register-existing-installation",roots=request.Backend=="flux-schnell"?new[]{request.ModelDirectory,request.NativeRuntimeRoot}:new[]{request.ModelDirectory,request.EnvironmentRoot,request.PythonBaseRoot},
            requiredComponents=definition["files"]!.DeepClone(),source=definition["source"]?.DeepClone(),
            environment=request.Backend is "stable-audio-open" or "ace-step-turbo" or "qwen3-tts-customvoice" or "qwen3-tts-base"?definition["environments"]!.DeepClone():null,
            sourceRoot=request.SourceRoot,sourceArchive=request.SourceArchive,
            receipt=request.ReceiptPath,execute,downloads=false,installation=false,generation=false,licenseAcceptance=false,promotion=false};
        if(!execute)return Task.FromResult<object>(plan);
        if(!host.AllowProvisioning)throw new InvalidOperationException("Explicit setup execution requires host provisioning authorization");
        cancellation.ThrowIfCancellationRequested();
        var inventory=new Dictionary<string,Bubo.Media.Application.InstalledFile>(StringComparer.Ordinal);
        foreach(var root in roots)
        {
            if(!Directory.Exists(root))throw new InvalidOperationException("Dependency directory missing; follow the pinned installation/build instructions before registration");
            foreach(var file in InstallationVerifier.FilesUnder(host,root))
            {
                cancellation.ThrowIfCancellationRequested();var relative=workspace.Relative(file);
                if(inventory.Count>=65536)throw new InvalidOperationException("Installation inventory exceeds65536files");
                inventory.Add(relative,new(relative,workspace.HashProjectFile(relative),new FileInfo(file).Length));
                if(inventory.Count%2000==0)Console.Error.WriteLine("Setup inventory verified "+inventory.Count+" local files");
            }
        }
        if(request.PythonArchive is {} archive)
        {
            var path=host.ResolveReadPath(archive);if(!File.Exists(path))throw new InvalidOperationException("Pinned base Python archive missing");
            inventory.Add(workspace.Relative(path),new(workspace.Relative(path),workspace.HashProjectFile(archive),new FileInfo(path).Length));
        }
        if(request.SourceArchive is {} sourceArchive)
        {
            var path=host.ResolveReadPath(sourceArchive);
            if(!File.Exists(path))throw new InvalidOperationException("Pinned engine source archive missing");
            inventory.Add(workspace.Relative(path),new(workspace.Relative(path),workspace.HashProjectFile(sourceArchive),new FileInfo(path).Length));
        }
        var receipt=new {schemaVersion="bubo-media-installation/v1",backend=request.Backend,platform=OperatingSystem.IsWindows()?"windows":"linux",
            catalogSha256=Workspace.Hash(catalogPath),modelDirectory=request.ModelDirectory,licenses=request.Licenses,
            python=request.Python,environmentRoot=request.EnvironmentRoot,pythonBaseRoot=request.PythonBaseRoot,wheelhouseRoot=request.WheelhouseRoot,pythonArchive=request.PythonArchive,noticeDirectory=request.NoticeDirectory,nativeRuntimeRoot=request.NativeRuntimeRoot,nativeWorker=request.NativeWorker,
            sourceRoot=request.SourceRoot,sourceArchive=request.SourceArchive,
            environmentSha256=request.EnvironmentRoot is {} environment?InstallationVerifier.Digest(inventory.Values.Where(x=>MediaAdmission.Within(Path.GetFullPath(x.Path,host.Root),Path.GetFullPath(environment,host.Root))||MediaAdmission.Within(Path.GetFullPath(x.Path,host.Root),Path.GetFullPath(request.PythonBaseRoot!,host.Root)))):null,
            files=inventory.Values.OrderBy(x=>x.Path,StringComparer.Ordinal).ToArray(),
            assurance="hash-bound installed inventory; local user/tool records are not signed build or license attestations",registeredAt=DateTimeOffset.UtcNow};
        // Validate the exact proposed bytes before publishing the caller-selected immutable receipt.
        var serialized=JsonSerializer.SerializeToUtf8Bytes(receipt,MediaJson.Options);
        var receiptHash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(serialized));
        Console.Error.WriteLine("Checking pinned model, runtime and wheel contents before registration");
        _=new InstallationVerifier(workspace,resources).VerifyInstalled(request.Backend,new(request.ReceiptPath,receiptHash),JsonNode.Parse(serialized)!.AsObject());
        cancellation.ThrowIfCancellationRequested();host.ValidateFresh();destination=host.ResolveWritePath(request.ReceiptPath);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        destination=host.ResolveWritePath(request.ReceiptPath);
        using(var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.Read))output.Write(serialized);
        // The caller registers the resulting receipt as an input in a fresh execution context before inspection/generation.
        return Task.FromResult<object>(new {schemaVersion="bubo-media-setup-result/v1",status="registered-pending-inspection",backend=request.Backend,
            receipt=request.ReceiptPath,sha256=Workspace.Hash(destination),files=inventory.Count,
            next="Register the receipt as a hash-bound input, then run media inspect; use --load true for a zero-sample load/unload probe",generation=false,promotion=false});
    }
}
