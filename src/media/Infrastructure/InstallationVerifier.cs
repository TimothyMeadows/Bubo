using System.Text.Json.Nodes;
using Bubo.Media.Application;
using Bubo.Media.Domain;
using OpenCaw.AssetProduction;
using HostExecutionContext = Bubo.Host.HostExecutionContext;

namespace Bubo.Media.Infrastructure;

public sealed class InstallationVerifier(IWorkspace workspace,AuditedResources resources):IInstallationVerifier
{
    public PreparedInstallation Verify(string backend,FileIdentity identity)
    {
        identity.Validate();var host=workspace.HostContext??throw new InvalidOperationException("Explicit host context required");
        host.ValidateFresh();host.RequireReference(identity.Path,"input");
        if(workspace.HashProjectFile(identity.Path)!=identity.Sha256)throw new InvalidOperationException("Installation receipt changed");
        var receipt=Read(host.ResolveReadPath(identity.Path),32*1024*1024);
        return VerifyInstalled(backend,identity,receipt);
    }
    internal PreparedInstallation VerifyInstalled(string backend,FileIdentity identity,JsonObject receipt)
    {
        var host=workspace.HostContext??throw new InvalidOperationException("Explicit host context required");host.ValidateFresh();
        var catalogPath=resources.Resolve("resources/media/catalog.json");var catalog=Read(catalogPath);
        var catalogHash=Workspace.Hash(catalogPath);
        if(Text(receipt,"schemaVersion")!="bubo-media-installation/v1" || Text(receipt,"backend")!=backend || Text(receipt,"catalogSha256")!=catalogHash)
            throw new InvalidOperationException("Installation belongs to another backend/catalog; explicitly refresh setup");
        var platform=OperatingSystem.IsWindows()?"windows":OperatingSystem.IsLinux()?"linux":throw new PlatformNotSupportedException("Media native ownership supports Windows/Linux only");
        if(Text(receipt,"platform")!=platform)throw new InvalidOperationException("Installation platform differs");
        var definition=catalog["backends"]!.AsArray().OfType<JsonObject>().SingleOrDefault(x=>Text(x,"id")==backend)
            ??throw new InvalidOperationException("Backend not in pinned catalog");
        var model=host.ResolveReadPath(Text(receipt,"modelDirectory"));
        if(!Directory.Exists(model))throw new InvalidOperationException("Pinned local model directory is missing");
        var expected=definition["files"]!.AsArray().OfType<JsonObject>().ToArray();
        var components=new Dictionary<string,string>(StringComparer.Ordinal);var inventory=new Dictionary<string,InstalledFile>(StringComparer.Ordinal);
        foreach(var value in receipt["files"]?.AsArray()??throw new InvalidOperationException("Complete installed file inventory required"))
        {
            var item=value!.AsObject();var path=Text(item,"path");var hash=Text(item,"sha256");var size=item["bytes"]!.GetValue<long>();
            if(hash.Length!=64 || !hash.All(char.IsAsciiHexDigit) || size<0 || !inventory.TryAdd(path,new(path,hash,size)))
                throw new InvalidOperationException("Invalid or duplicated installed file identity");
        }
        if(inventory.Count is <1 or >65536)throw new InvalidOperationException("Installed file inventory exceeds its bound");
        var observed=VerifiedFiles.Capture(host,inventory.Values);
        foreach(var item in expected)
        {
            var relative=Text(item,"path");var path=Path.Combine(model,relative.Replace('/',Path.DirectorySeparatorChar));
            var projectPath=workspace.Relative(path);
            if(!MediaAdmission.Within(path,model) || !inventory.TryGetValue(projectPath,out var actual)
                || actual.Sha256!=Text(item,"sha256") || actual.Bytes!=item["bytes"]!.GetValue<long>())
                throw new InvalidOperationException("Model component differs from the pinned snapshot: "+relative);
            components.Add(relative,path);
        }
        var actualModelFiles=FilesUnder(host,model).Select(p=>Path.GetRelativePath(model,p).Replace('\\','/')).Order(StringComparer.Ordinal);
        if(!actualModelFiles.SequenceEqual(expected.Select(x=>Text(x,"path")).Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Model directory has missing/extra files; no alternate pickle or callback files are admitted");
        var licenses=receipt["licenses"]?.AsArray()??throw new InvalidOperationException("Explicit license evidence required");
        if(licenses.Count==0)throw new InvalidOperationException("No license evidence supplied");
        var accepted=new HashSet<string>(StringComparer.Ordinal);
        foreach(var node in licenses)
        {
            var file=node!.AsObject();var path=Text(file,"path");host.RequireReference(path,"rights");
            if(workspace.HashProjectFile(path)!=Text(file,"sha256"))throw new InvalidOperationException("License evidence changed");
            var evidence=Read(host.ResolveReadPath(path));
            if(Text(evidence,"schemaVersion")!="bubo-media-license-evidence/v1" || evidence["accepted"]?.GetValue<bool>()!=true
                || evidence["backends"]?.AsArray().Any(x=>x!.GetValue<string>()==backend)!=true)
                throw new InvalidOperationException("Applicable explicit license approval is required");
            foreach(var id in evidence["licenseIds"]!.AsArray())accepted.Add(id!.GetValue<string>());
        }
        foreach(var item in expected)
            if(!accepted.Contains(Text(item,"license")))throw new InvalidOperationException("A selected component license is not approved");
        if(receipt["noticeDirectory"] is JsonValue noticeValue && noticeValue.TryGetValue<string>(out var noticeDirectory))VerifyInventoryRoot(host,host.ResolveReadPath(noticeDirectory),inventory);
        if(platform=="windows")
        {
            var notices=host.ResolveReadPath(receipt["noticeDirectory"]?.GetValue<string>()??(backend=="flux-schnell"?Text(receipt,"nativeRuntimeRoot"):throw new InvalidOperationException("Windows SFX needs its inventoried runtime notice directory")));
            foreach(var item in catalog["windowsRuntime"]!["notices"]!.AsArray().OfType<JsonObject>())
            {
                var path=workspace.Relative(Path.Combine(notices,Text(item,"path")));
                if(!inventory.TryGetValue(path,out var actual)||actual.Sha256!=Text(item,"sha256")||actual.Bytes!=item["bytes"]!.GetValue<long>())throw new InvalidOperationException("Pinned runtime license notice missing/changed");
            }
        }
        string? python=null;string worker;string environmentHash;
        if(backend=="stable-audio-open")
        {
            var pythonRecord=receipt["python"]!.AsObject();python=Path.GetFullPath(Text(pythonRecord,"path"),host.Root);host.EnsureToolAllowed(python);
            if(host.HashToolFile(python)!=Text(pythonRecord,"sha256") || Text(pythonRecord,"version")!="3.12.16")throw new InvalidOperationException("Pinned Python3.12.16 identity differs");
            worker=resources.Resolve(Text(definition,"workerSource"));
            var envPath=definition["environments"]![platform]!.GetValue<string>();
            if(resources.Hash(envPath)!=definition["environmentSha256"]![platform]!.GetValue<string>())throw new InvalidOperationException("Backend environment specification changed");
            var envSpec=Read(resources.Resolve(envPath));
            var environment=host.ResolveReadPath(Text(receipt,"environmentRoot"));
            var pythonBase=host.ResolveReadPath(Text(receipt,"pythonBaseRoot"));
            if(!MediaAdmission.Within(python,environment) || MediaAdmission.Within(environment,pythonBase) || MediaAdmission.Within(pythonBase,environment))
                throw new InvalidOperationException("Python must belong to its separate dedicated environment");
            VerifyInventoryRoot(host,pythonBase,inventory);
            VerifyEnvironment(host,environment,inventory,envSpec);
            var wheelhouse=host.ResolveReadPath(Text(receipt,"wheelhouseRoot"));VerifyInventoryRoot(host,wheelhouse,inventory);
            var archive=host.ResolveReadPath(Text(receipt,"pythonArchive"));
            if(!inventory.ContainsKey(workspace.Relative(archive)))throw new InvalidOperationException("Base runtime archive is not inventoried");
            var runtime=catalog["pythonRuntimes"]![platform=="windows"?"x86_64-pc-windows-msvc":"x86_64-unknown-linux-gnu"]!.AsObject();
            PythonIntegrity.Verify(host,environment,pythonBase,wheelhouse,archive,envSpec,runtime,catalog,observed);
            var config=Path.Combine(environment,"pyvenv.cfg");
            if(!File.Exists(config))throw new InvalidOperationException("Isolated Python environment configuration missing");
            var settings=File.ReadLines(config).Where(x=>x.Contains('=')).Select(x=>x.Split('=',2)).ToDictionary(x=>x[0].Trim(),x=>x[1].Trim(),StringComparer.OrdinalIgnoreCase);
            if(!settings.TryGetValue("include-system-site-packages",out var include) || include!="false"
                || !settings.TryGetValue("home",out var home) || !Path.GetFullPath(home).Equals(OperatingSystem.IsWindows()?pythonBase:Path.Combine(pythonBase,"bin"),OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
                throw new InvalidOperationException("Venv must exclude system packages and bind the inventoried base Python");
            environmentHash=Text(receipt,"environmentSha256");
            // Pure inventory classification; the files were admitted and hashed above.
            var actualDigest=Digest(inventory.Values.Where(x=>MediaAdmission.Within(Path.GetFullPath(x.Path,host.Root),environment)||MediaAdmission.Within(Path.GetFullPath(x.Path,host.Root),pythonBase)));
            if(actualDigest!=environmentHash)throw new InvalidOperationException("Python environment inventory fingerprint changed");
        }
        else if(backend=="flux-schnell")
        {
            var native=receipt["nativeWorker"]!.AsObject();worker=Path.GetFullPath(Text(native,"path"),host.Root);host.EnsureToolAllowed(worker);
            var bundle=host.ResolveReadPath(Text(receipt,"nativeRuntimeRoot"));
            if(!Path.GetDirectoryName(worker)!.Equals(bundle,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new InvalidOperationException("Worker must be directly beside its inventoried runtime dependencies");
            VerifyInventoryRoot(host,bundle,inventory);
            if(OperatingSystem.IsWindows())foreach(var item in catalog["windowsRuntime"]!["files"]!.AsArray().OfType<JsonObject>().Where(x=>x["backends"]!.AsArray().Any(b=>b!.GetValue<string>()==backend)))
            {
                var path=host.ResolveReadPath(Path.Combine(bundle,Text(item,"path")));
                if(!File.Exists(path)||new FileInfo(path).Length!=item["bytes"]!.GetValue<long>()||Workspace.Hash(path)!=Text(item,"sha256"))throw new InvalidOperationException("Required pinned native runtime dependency differs: "+path);
            }
            if(host.HashToolFile(worker)!=Text(native,"sha256") || Text(native,"workerSourceSha256")!=resources.Hash(Text(definition,"workerSource"))
                || Text(native,"workerBuildSha256")!=resources.Hash(Text(definition,"workerBuild")))
                throw new InvalidOperationException("Native worker/build selection differs from the audited source");
            foreach(var key in new[]{"revision","ggmlRevision","cutlassRevision"})
                if(Text(native,key)!=Text(definition["source"]!.AsObject(),key))throw new InvalidOperationException("Native source revision differs");
            environmentHash=Digest(inventory.Values.Where(x=>!MediaAdmission.Within(Path.GetFullPath(x.Path,host.Root),model)));
        }
        else throw new InvalidOperationException("Backend not implemented");
        var immutableRoots=new List<string>{model};
        foreach(var key in new[]{"nativeRuntimeRoot","pythonBaseRoot","environmentRoot","wheelhouseRoot","pythonArchive","noticeDirectory"})
            if(receipt[key] is JsonValue value && value.TryGetValue<string>(out var path))immutableRoots.Add(host.ResolveReadPath(path));
        return new(backend,identity.Path,identity.Sha256,worker,model,python,components,catalogHash,environmentHash,inventory.Values.ToArray(),Workspace.Hash(worker),resources.Hash(Text(definition,"workerSource")),resources.CatalogSha256,immutableRoots);
    }
    static void VerifyEnvironment(HostExecutionContext host,string root,IReadOnlyDictionary<string,InstalledFile> inventory,JsonObject spec)
    {
        var files=VerifyInventoryRoot(host,root,inventory);
        var versions=new Dictionary<string,string>(StringComparer.Ordinal);
        var site=Path.GetFullPath(Path.Combine(root,OperatingSystem.IsWindows()?"Lib/site-packages":"lib/python3.12/site-packages"));
        foreach(var path in files.Where(p=>Path.GetFileName(p)=="METADATA" && Path.GetFileName(Path.GetDirectoryName(p)!).EndsWith(".dist-info",StringComparison.Ordinal)
            && Path.GetDirectoryName(Path.GetDirectoryName(p))!.Equals(site,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)))
        {
            var lines=File.ReadLines(path).TakeWhile(x=>x.Length>0).ToArray();
            var name=lines.Single(x=>x.StartsWith("Name: ",StringComparison.Ordinal))[6..];
            var version=lines.Single(x=>x.StartsWith("Version: ",StringComparison.Ordinal))[9..];
            versions.Add(Canonical(name),version);
        }
        var expected=spec["artifacts"]!.AsArray().OfType<JsonObject>().ToDictionary(x=>Text(x,"name"),x=>Text(x,"version"),StringComparer.Ordinal);
        foreach(var (name,version) in expected)
            if(!versions.TryGetValue(name,out var actual) || actual!=version)throw new InvalidOperationException("Pinned Python dependency differs: "+name);
        if(versions.Keys.Any(x=>!expected.ContainsKey(x) && x!="pip"))throw new InvalidOperationException("Unexpected Python distribution in isolated environment");
    }
    static string[] VerifyInventoryRoot(HostExecutionContext host,string root,IReadOnlyDictionary<string,InstalledFile> inventory)
    {
        if(!Directory.Exists(root))throw new InvalidOperationException("Installed runtime root missing");
        var files=FilesUnder(host,root).ToArray();
        if(files.Length==0)throw new InvalidOperationException("Installed runtime root empty");
        foreach(var path in files)
            if(!inventory.ContainsKey(Path.GetRelativePath(host.Root,path).Replace('\\','/')))throw new InvalidOperationException("Runtime contains an unrecorded file: "+path);
        return files;
    }
    static string Canonical(string name)=>System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(),"[-_.]+","-");
    public static string Digest(IEnumerable<InstalledFile> files)
    {
        var text=string.Concat(files.OrderBy(x=>x.Path,StringComparer.Ordinal).Select(x=>x.Path+"|"+x.Sha256+"|"+x.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)+"\n"));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }
    public static IEnumerable<string> FilesUnder(HostExecutionContext host,string root)
    {
        var pending=new Stack<string>();pending.Push(root);
        while(pending.TryPop(out var directory))
        {
            host.EnsureReadAllowed(directory);
            foreach(var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var full=host.ResolveReadPath(path);var attributes=File.GetAttributes(full);
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Linked installed dependencies are forbidden");
                if((attributes&FileAttributes.Directory)!=0)pending.Push(full);else yield return full;
            }
        }
    }
    static JsonObject Read(string path,int maximum=1048576)
    {
        using var input=File.OpenRead(path);if(input.Length is <2 || input.Length>maximum)throw new InvalidOperationException("Unbounded installation/catalog document");
        return JsonNode.Parse(input,documentOptions:new(){MaxDepth=32})!.AsObject();
    }
    public static string Text(JsonObject value,string name)=>value[name]?.GetValue<string>() is {Length:>0} text?text:throw new InvalidOperationException("Missing installation/catalog field "+name);
}
