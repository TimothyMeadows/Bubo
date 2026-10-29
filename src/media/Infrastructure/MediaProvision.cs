using System.Text.Json;
using Bubo.Media.Application;
using Bubo.Media.Domain;
using OpenCaw.AssetProduction;

namespace Bubo.Media.Infrastructure;

/// <summary>Separately authorized setup. The fixed installer may download/build; production workers cannot invoke it.</summary>
public sealed class MediaProvision(IWorkspace workspace,AuditedResources resources,INativeProcess process)
{
    public sealed record ProvisionRequest(string SchemaVersion,string Task,string Backend,string InstallationRoot,string DownloadRoot,
        FileIdentity BootstrapPython,FileIdentity[] Licenses,FileIdentity? Git=null,FileIdentity? Cmake=null,FileIdentity? CompilerEnvironment=null,string? CudaRoot=null,FileIdentity? CudaCompiler=null,FileIdentity? CommandShell=null);
    public async Task<object> Execute(string requestPath,bool execute,CancellationToken cancellation)
    {
        var host=workspace.HostContext??throw new InvalidOperationException("Host context required");host.ValidateFresh();
        if(!host.MediaOnly || host.AllowPromotion)throw new InvalidOperationException("Provisioning requires local-media purpose without promotion");
        host.RequireReference(requestPath,"input");
        var request=MediaJson.Read<ProvisionRequest>(MediaAdmission.ReadBounded(host.ResolveReadPath(requestPath)));_=MediaCapabilities.Require(request.Backend);
        if(request.SchemaVersion!="bubo-media-provision/v1" || request.Task!=host.TaskId || request.Licenses is not {Length:>0 and <=16})throw new InvalidOperationException("Invalid scoped provisioning request");
        var install=host.ResolveWritePath(request.InstallationRoot);var download=host.ResolveWritePath(request.DownloadRoot);
        if(!host.ToolRoots.Any(x=>MediaAdmission.Within(install,host.ResolveReadPath(x))) || !host.ToolRoots.Any(x=>MediaAdmission.Within(download,host.ResolveReadPath(x))))
            throw new InvalidOperationException("Provisioning may write only declared project tool roots");
        if(MediaAdmission.Within(install,download)||MediaAdmission.Within(download,install))throw new InvalidOperationException("Install and download directories must be disjoint");
        foreach(var identity in request.Licenses)
        {
            identity.Validate();host.RequireReference(identity.Path,"rights");if(workspace.HashProjectFile(identity.Path)!=identity.Sha256)throw new InvalidOperationException("License evidence changed");
        }
        var tools=new[]{request.BootstrapPython,request.Git,request.Cmake,request.CompilerEnvironment,request.CudaCompiler,request.CommandShell}.OfType<FileIdentity>().ToArray();
        foreach(var tool in tools)
        {
            tool.Validate();var path=Path.GetFullPath(tool.Path,host.Root);host.EnsureToolAllowed(path);if(host.HashToolFile(path)!=tool.Sha256)throw new InvalidOperationException("Provisioning tool identity changed");
        }
        if(OperatingSystem.IsWindows() && (request.CompilerEnvironment is null || request.CommandShell is null))throw new InvalidOperationException("Windows setup requires an explicit existing registered Visual C++ environment and system command shell");
        if(OperatingSystem.IsWindows() && !Path.GetFullPath(request.CommandShell!.Path,host.Root).Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"cmd.exe"),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Windows compiler setup requires the registered System32 command shell");
        if(request.Backend=="flux-schnell" && (request.Git is null || request.Cmake is null || request.CudaRoot is null || request.CudaCompiler is null))
            throw new InvalidOperationException("Native build needs explicit registered Git/CMake/compiler environment and existing CUDA root; no compiler/driver installation");
        if(request.CudaCompiler is {} cudaCompiler && !Path.GetFullPath(cudaCompiler.Path,host.Root).Equals(Path.Combine(Path.GetFullPath(request.CudaRoot!),OperatingSystem.IsWindows()?"bin/nvcc.exe":"bin/nvcc"),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new InvalidOperationException("Registered CUDA compiler must belong to the selected existing toolkit");
        var script=resources.Resolve("resources/media/provision.py");var catalog=resources.Resolve("resources/media/catalog.json");
        var platform=OperatingSystem.IsWindows()?"windows":OperatingSystem.IsLinux()?"linux":throw new PlatformNotSupportedException("Provisioning supports Windows/Linux only");
        var environment=request.Backend=="stable-audio-open"?resources.Resolve("resources/media/environments/sfx-"+platform+".json"):null;
        var plan=new {schemaVersion="bubo-media-provision-plan/v1",request.Backend,platform,installationRoot=request.InstallationRoot,downloadRoot=request.DownloadRoot,
            catalogSha256=Workspace.Hash(catalog),environmentSha256=environment is null?null:Workspace.Hash(environment),installerSha256=Workspace.Hash(script),
            execute,networkAccess=execute,driverInstallation=false,licenseAcceptance=false,generation=false,promotion=false};
        if(!execute)return plan;
        if(!host.AllowProvisioning)throw new InvalidOperationException("Explicit provisioning authorization required");
        if(Directory.Exists(install)||File.Exists(install))throw new InvalidOperationException("Installation identity already exists; preserve it and select a fresh owned path");
        var state=host.StateRoot+"/media-provision/"+Guid.NewGuid().ToString("N");
        var job=state+"/job.json";var result=state+"/result.json";
        workspace.Save(job,new {schemaVersion="bubo-media-provision-worker/v1",backend=request.Backend,installRoot=install,downloadRoot=download,
            projectRoot=host.Root,resultPath=workspace.ResolveWrite(result),
            catalog,catalogSha256=Workspace.Hash(catalog),environment,environmentSha256=environment is null?null:Workspace.Hash(environment),
            workerSource=resources.Resolve("resources/media/image/worker.cpp"),workerBuild=resources.Resolve("resources/media/image/worker.cmake"),
            sfxWorker=resources.Resolve("resources/media/sfx/worker.py"),
            resourceIdentities=new[]{script,catalog,environment,resources.Resolve("resources/media/image/worker.cpp"),resources.Resolve("resources/media/image/worker.cmake"),resources.Resolve("resources/media/sfx/worker.py")}.OfType<string>().Select(x=>new{path=x,sha256=Workspace.Hash(x)}).ToArray(),
            toolIdentities=tools.Select(x=>new{path=Path.GetFullPath(x.Path,host.Root),x.Sha256}).ToArray(),
            licenses=request.Licenses.Select(x=>new{path=host.ResolveReadPath(x.Path),x.Sha256}).ToArray(),
            git=request.Git is null?null:Path.GetFullPath(request.Git.Path,host.Root),cmake=request.Cmake is null?null:Path.GetFullPath(request.Cmake.Path,host.Root),
            compilerEnvironment=request.CompilerEnvironment is null?null:Path.GetFullPath(request.CompilerEnvironment.Path,host.Root),commandShell=request.CommandShell is null?null:Path.GetFullPath(request.CommandShell.Path,host.Root),cudaRoot=request.CudaRoot});
        await process.Run(Path.GetFullPath(request.BootstrapPython.Path,host.Root),["-I","-B","-X","utf8",script,workspace.Resolve(job),workspace.ResolveWrite(result),workspace.HashProjectFile(job)],host.Root,
            workspace.ResolveWrite(state+"/setup.log"),host.TimeoutSeconds,cancellation,host.MaxLogBytes,
            new Dictionary<string,string>{{"PYTHONDONTWRITEBYTECODE","1"},{"PIP_DISABLE_PIP_VERSION_CHECK","1"},{"PIP_CONFIG_FILE",OperatingSystem.IsWindows()?"NUL":"/dev/null"}});
        host.ValidateFresh();
        var outcome=JsonSerializer.Deserialize<JsonElement>(MediaAdmission.ReadBounded(workspace.Resolve(result)));
        return new {schemaVersion="bubo-media-provision-result/v1",status="installed-pending-registration",plan,result=workspace.Relative(result),data=outcome,
            next="Prepare a bubo-media-setup/v1 registration request for the installed roots, then inspect the registered installation",generation=false,promotion=false};
    }
}
