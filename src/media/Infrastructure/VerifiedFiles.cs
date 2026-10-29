using Bubo.Media.Application;
using Bubo.Host;
using OpenCaw.AssetProduction;
using HostExecutionContext=Bubo.Host.HostExecutionContext;

namespace Bubo.Media.Infrastructure;

/// <summary>Operation-local observations, created only by guarded fresh reads. Never serialized or cached between calls.</summary>
internal sealed class VerifiedFiles
{
    readonly Dictionary<string,InstalledFile> files=new(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
    VerifiedFiles() { }
    public InstalledFile At(string path)=>files.TryGetValue(Path.GetFullPath(path),out var file)?file:throw new InvalidOperationException("File was not freshly observed in this operation: "+path);
    public static VerifiedFiles Capture(HostExecutionContext host,IEnumerable<InstalledFile> expected)
    {
        var result=new VerifiedFiles();
        foreach(var file in expected)
        {
            var path=host.ResolveReadPath(file.Path);
            if(!File.Exists(path))throw new InvalidOperationException("Installed dependency is missing: "+file.Path);
            var bytes=new FileInfo(path).Length;var hash=Workspace.Hash(path);
            if(bytes!=file.Bytes || hash!=file.Sha256)throw new InvalidOperationException("Installed dependency is modified: "+file.Path);
            if(!result.files.TryAdd(path,new(path,hash,bytes)))throw new InvalidOperationException("Aliased installed file identities are forbidden");
        }
        return result;
    }
    public static VerifiedFiles Observe(HostExecutionContext host,IEnumerable<string> paths)
    {
        var result=new VerifiedFiles();
        foreach(var value in paths)
        {
            var path=host.ResolveReadPath(value);var bytes=new FileInfo(path).Length;var hash=Workspace.Hash(path);
            if(!result.files.TryAdd(path,new(path,hash,bytes)))throw new InvalidOperationException("Aliased observed file identities are forbidden");
        }
        return result;
    }
}
