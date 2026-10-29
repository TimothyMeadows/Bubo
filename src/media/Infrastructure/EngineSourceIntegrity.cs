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
        if(source.ContainsKey("adaptations") && source["adaptations"] is not JsonArray)
            throw new InvalidOperationException("Engine source adaptations require an explicit array");
        var declarations=source["adaptations"]?.AsArray()??[];
        if(declarations.Count>16 || declarations.Any(node=>node is not JsonObject))
            throw new InvalidOperationException("Engine source adaptations require bounded object declarations");
        var adaptations=new Dictionary<string,JsonObject>(StringComparer.Ordinal);
        foreach(var declaration in declarations.Cast<JsonObject>())
        {
            var target=declaration["path"]?.GetValue<string>()??throw new InvalidOperationException("Missing engine source adaptation path");
            if(!adaptations.TryAdd(target,declaration))throw new InvalidOperationException("Duplicate engine source adaptation path");
        }
        var adapted=new HashSet<string>(StringComparer.Ordinal);
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
            var originalHash=Convert.ToHexStringLower(SHA256.HashData(contents));
            if(adaptations.TryGetValue(relative,out var change))
            {
                var retained=change["originalRetainedPath"]!.GetValue<string>();
                if(!retained.StartsWith(".originals/",StringComparison.Ordinal)||retained.Contains('\\')||retained.Split('/').Any(part=>part is "" or "." or "..")
                    ||originalHash!=change["originalSha256"]!.GetValue<string>()||!expected.Add(retained)||!adapted.Add(relative))
                    throw new InvalidOperationException("Invalid explicit engine source adaptation ancestry");
                var originalPath=host.ResolveReadPath(Path.Combine(sourceRoot,retained.Replace('/',Path.DirectorySeparatorChar)));
                if(!MediaAdmission.Within(originalPath,sourceRoot)||!canonical.Add(originalPath))throw new InvalidOperationException("Aliased engine source original");
                var original=observed.At(originalPath);
                if(original.Sha256!=originalHash||original.Bytes!=entry.Length||file.Sha256!=change["replacementSha256"]!.GetValue<string>()||file.Bytes!=change["replacementBytes"]!.GetValue<long>())
                    throw new InvalidOperationException("Engine source adaptation or retained original differs: "+relative);
            }
            else if(file.Bytes!=entry.Length || file.Sha256!=originalHash)
                throw new InvalidOperationException("Engine source differs from its exact pinned archive: "+relative);
        }
        if(!adapted.SetEquals(adaptations.Keys))throw new InvalidOperationException("Engine source adaptation does not match an actual archived file");
        var actual=InstallationVerifier.FilesUnder(host,sourceRoot).Select(x=>Path.GetRelativePath(sourceRoot,x).Replace('\\','/')).Order(StringComparer.Ordinal);
        if(!actual.SequenceEqual(expected.Order(StringComparer.Ordinal)))throw new InvalidOperationException("Engine source has missing or extra files");
    }
}
