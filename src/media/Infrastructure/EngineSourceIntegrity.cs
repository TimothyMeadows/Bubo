using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bubo.Media.Application;
using HostExecutionContext=Bubo.Host.HostExecutionContext;

namespace Bubo.Media.Infrastructure;

/// <summary>Closed source-tree verification against the exact pinned upstream ZIP; not an editable import path.</summary>
internal static class EngineSourceIntegrity
{
    public static void Verify(HostExecutionContext host,string sourceRoot,string archive,JsonObject source,VerifiedFiles observed)
    {
        var expectedHash=source["archiveSha256"]!.GetValue<string>();
        var expectedBytes=source["archiveBytes"]!.GetValue<long>();
        var prefix=source["archiveRootPrefix"]!.GetValue<string>();
        var captured=observed.At(archive);
        if(captured.Sha256!=expectedHash || captured.Bytes!=expectedBytes || prefix.Length<2 || !prefix.EndsWith('/'))
            throw new InvalidOperationException("Engine source archive differs from the pinned revision");
        using var input=new FileStream(archive,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length!=expectedBytes || Convert.ToHexStringLower(SHA256.HashData(input))!=expectedHash)
            throw new InvalidOperationException("Engine source archive changed before content verification");
        input.Position=0;
        using var zip=new ZipArchive(input,ZipArchiveMode.Read);
        var expected=new HashSet<string>(StringComparer.Ordinal);
        var canonical=new HashSet<string>(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
        long expanded=0;
        foreach(var entry in zip.Entries)
        {
            if(!entry.FullName.StartsWith(prefix,StringComparison.Ordinal))throw new InvalidOperationException("Unexpected engine source archive root");
            var relative=entry.FullName[prefix.Length..];
            if(relative.Length==0 || relative.EndsWith('/'))continue;
            if(((entry.ExternalAttributes>>16)&0xF000)==0xA000 || relative.Contains('\\') || relative.Split('/').Any(x=>x is "" or "." or ".."))
                throw new InvalidOperationException("Linked or escaping engine source archive entry");
            expanded=checked(expanded+entry.Length);
            if(expanded>268435456 || expected.Count>=5000 || !expected.Add(relative))throw new InvalidOperationException("Engine source archive exceeds its closed bounds");
            var path=host.ResolveReadPath(Path.Combine(sourceRoot,relative.Replace('/',Path.DirectorySeparatorChar)));
            if(!MediaAdmission.Within(path,sourceRoot) || !canonical.Add(path))throw new InvalidOperationException("Aliased engine source entry");
            var file=observed.At(path);
            using var contents=entry.Open();
            if(file.Bytes!=entry.Length || file.Sha256!=Convert.ToHexStringLower(SHA256.HashData(contents)))
                throw new InvalidOperationException("Engine source differs from its exact pinned archive: "+relative);
        }
        var actual=InstallationVerifier.FilesUnder(host,sourceRoot).Select(x=>Path.GetRelativePath(sourceRoot,x).Replace('\\','/')).Order(StringComparer.Ordinal);
        if(!actual.SequenceEqual(expected.Order(StringComparer.Ordinal)))throw new InvalidOperationException("Engine source has missing or extra files");
    }
}
