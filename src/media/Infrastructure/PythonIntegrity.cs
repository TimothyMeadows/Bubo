using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bubo.Host;
using HostExecutionContext=Bubo.Host.HostExecutionContext;

namespace Bubo.Media.Infrastructure;

/// <summary>Installed executable content derives from pinned archives, rather than an installer's own RECORD claims.</summary>
internal static class PythonIntegrity
{
    public static void Verify(HostExecutionContext host,string environment,string baseRoot,string wheelhouse,string archive,JsonObject spec,JsonObject runtime,JsonObject catalog)
        =>Verify(host,environment,baseRoot,wheelhouse,archive,spec,runtime,catalog,
            VerifiedFiles.Observe(host,InstallationVerifier.FilesUnder(host,environment).Concat(InstallationVerifier.FilesUnder(host,baseRoot)).Concat(InstallationVerifier.FilesUnder(host,wheelhouse)).Append(archive)));
    public static void Verify(HostExecutionContext host,string environment,string baseRoot,string wheelhouse,string archive,JsonObject spec,JsonObject runtime,JsonObject catalog,VerifiedFiles observed)
    {
        var windows=OperatingSystem.IsWindows();var comparison=windows?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
        var allowedBase=new HashSet<string>(comparison);var expectedBase=new Dictionary<string,string>(StringComparer.Ordinal);var links=new Dictionary<string,string>(StringComparer.Ordinal);
        if(new FileInfo(archive).Length!=runtime["bytes"]!.GetValue<long>() || Hash(archive)!=runtime["sha256"]!.GetValue<string>())throw new InvalidOperationException("Base Python archive differs from pinned runtime");
        using(var input=File.OpenRead(archive))using(var gzip=new GZipStream(input,CompressionMode.Decompress))using(var tar=new TarReader(gzip))
        {
            TarEntry? entry;while((entry=tar.GetNextEntry()) is not null)
            {
                var name=entry.Name.TrimEnd('/');if(name=="python")continue;
                if(!name.StartsWith("python/",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected Python archive root");
                var relative=name[7..];var target=Safe(host,baseRoot,relative);
                if(entry.EntryType==TarEntryType.Directory)continue;
                if(entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    var linked=entry.EntryType==TarEntryType.HardLink?entry.LinkName.Replace('\\','/').Replace("python/",""):Path.GetRelativePath(baseRoot,host.ResolveReadPath(Path.GetFullPath(entry.LinkName,Path.GetDirectoryName(target)!))).Replace('\\','/');
                    _=Safe(host,baseRoot,linked);links.Add(relative,linked);continue;
                }
                if(entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null && entry.Length!=0)throw new InvalidOperationException("Unsupported Python archive entry: "+entry.EntryType+" "+relative);
                expectedBase.Add(relative,Convert.ToHexStringLower(SHA256.HashData(entry.DataStream??Stream.Null)));
            }
        }
        for(var pass=0;links.Count>0 && pass<32;pass++)foreach(var (path,target) in links.ToArray())
            if(expectedBase.TryGetValue(target,out var hash)){expectedBase.Add(path,hash);links.Remove(path);}
        if(links.Count>0)throw new InvalidOperationException("Unresolved/cyclic Python archive links; materialize safe pinned targets during explicit setup");
        // An explicitly pinned app-local CRT update is the sole allowed modification to the base archive.
        var backend=spec["backend"]!.GetValue<string>();
        var runtimeFiles=windows?catalog["windowsRuntime"]!["files"]!.AsArray().OfType<JsonObject>().Where(x=>x["backends"]!.AsArray().Any(b=>b!.GetValue<string>()==backend)).ToArray():[];
        foreach(var value in runtimeFiles)expectedBase[value["path"]!.GetValue<string>()]=value["sha256"]!.GetValue<string>();
        foreach(var (relative,hash) in expectedBase)
        {
            var path=Safe(host,baseRoot,relative);if(observed.At(path).Sha256!=hash)throw new InvalidOperationException("Base Python file differs from pinned archive/runtime: "+relative);allowedBase.Add(path);
        }
        if(InstallationVerifier.FilesUnder(host,baseRoot).Any(x=>!allowedBase.Contains(x)))throw new InvalidOperationException("Unverified extra base Python file");
        var site=Path.Combine(environment,windows?"Lib/site-packages":"lib/python3.12/site-packages");
        var allowed=new HashSet<string>(comparison);var distributionRoots=new HashSet<string>(comparison);
        var wheelFiles=InstallationVerifier.FilesUnder(host,wheelhouse).ToArray();
        var artifacts=spec["artifacts"]!.AsArray().OfType<JsonObject>().ToArray();
        if(wheelFiles.Length!=artifacts.Length)throw new InvalidOperationException("Wheelhouse contains missing/extra files");
        foreach(var artifact in artifacts)
        {
            var path=Safe(host,wheelhouse,artifact["filename"]!.GetValue<string>());
            if(!File.Exists(path)||new FileInfo(path).Length!=artifact["size"]!.GetValue<long>()||Hash(path)!=artifact["sha256"]!.GetValue<string>())throw new InvalidOperationException("Wheel archive differs from pinned dependency");
            Wheel(host,path,environment,site,allowed,distributionRoots,observed);
        }
        // ensurepip is part of the verified base runtime, and supplies the sole extra distribution.
        var pip=Directory.GetFiles(Path.Combine(baseRoot,windows?"Lib/ensurepip/_bundled":"lib/python3.12/ensurepip/_bundled"),"pip-*.whl");
        if(pip.Length!=1)throw new InvalidOperationException("Pinned bootstrap pip wheel missing/ambiguous");
        Wheel(host,pip[0],environment,site,allowed,distributionRoots,observed);
        foreach(var directory in distributionRoots)
        {
            var installer=Path.Combine(directory,"INSTALLER");if(File.Exists(installer)){if(File.ReadAllText(installer)!="pip\n")throw new InvalidOperationException("Unexpected package installer");allowed.Add(installer);}
            var requested=Path.Combine(directory,"REQUESTED");if(File.Exists(requested)){if(new FileInfo(requested).Length!=0)throw new InvalidOperationException("Unexpected REQUESTED metadata");allowed.Add(requested);}
            allowed.Add(Path.Combine(directory,"RECORD")); // unused generated index; wheel content above is the trust root
        }
        var executableDirectory=Path.Combine(environment,windows?"Scripts":"bin");
        if(windows)
        {
            foreach(var name in new[]{"python.exe","pythonw.exe"})
            {
                var path=Safe(host,executableDirectory,name);var source=Safe(host,baseRoot,"Lib/venv/scripts/nt/"+name);
                if(observed.At(path).Sha256!=observed.At(source).Sha256)throw new InvalidOperationException("Venv Python launcher differs from verified base");allowed.Add(path);
            }
            foreach(var value in runtimeFiles)
            {
                var path=Safe(host,executableDirectory,value["path"]!.GetValue<string>());if(observed.At(path).Sha256!=value["sha256"]!.GetValue<string>())throw new InvalidOperationException("Venv app-local CRT differs");allowed.Add(path);
            }
        }
        else foreach(var name in new[]{"python","python3","python3.12"})
        {
            var path=Safe(host,executableDirectory,name);if(observed.At(path).Sha256!=observed.At(Safe(host,baseRoot,"bin/python3.12")).Sha256)throw new InvalidOperationException("Venv requires copied pinned Python executables");allowed.Add(path);
        }
        allowed.Add(Path.Combine(environment,"pyvenv.cfg"));
        if(InstallationVerifier.FilesUnder(host,environment).Any(x=>!allowed.Contains(x)))throw new InvalidOperationException("Unverified installed file; use --no-compile and omit unused entrypoint launchers in this dedicated runtime");
    }
    static void Wheel(HostExecutionContext host,string wheel,string environment,string site,HashSet<string> allowed,HashSet<string> distributions,VerifiedFiles observed)
    {
        using var archive=ZipFile.OpenRead(wheel);
        foreach(var entry in archive.Entries)
        {
            var name=entry.FullName;if(name.EndsWith('/'))continue;
            string destination;
            var parts=name.Split('/');
            if(parts[0].EndsWith(".data",StringComparison.Ordinal))
            {
                if(parts.Length<3)throw new InvalidOperationException("Invalid wheel data entry");
                var relative=string.Join('/',parts[2..]);
                destination=parts[1] switch {"purelib" or "platlib"=>Safe(host,site,relative),"data"=>Safe(host,environment,relative),"scripts"=>null!,_=>throw new InvalidOperationException("Unsupported wheel installation scheme")};
                if(destination is null)continue; // unused launchers are deliberately not installed in the narrow worker runtime
            }
            else destination=Safe(host,site,name);
            if(parts[0].EndsWith(".dist-info",StringComparison.Ordinal))distributions.Add(Safe(host,site,parts[0]));
            if(name.EndsWith(".dist-info/RECORD",StringComparison.Ordinal)){allowed.Add(destination);continue;}
            var actual=observed.At(destination);if(actual.Bytes!=entry.Length)throw new InvalidOperationException("Installed wheel file missing/length differs: "+name);
            using var stream=entry.Open();if(actual.Sha256!=Convert.ToHexStringLower(SHA256.HashData(stream)))throw new InvalidOperationException("Installed wheel content differs: "+name);
            allowed.Add(destination);
        }
    }
    static string Safe(HostExecutionContext host,string root,string relative)
    {
        var path=host.ResolveReadPath(Path.GetFullPath(relative,root));
        if(!Bubo.Media.Application.MediaAdmission.Within(path,root))throw new InvalidOperationException("Archive path escapes its dependency root");return path;
    }
    static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexStringLower(SHA256.HashData(file));}
}
