using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Hephaera.AssetProduction;

public sealed record BundledGltfFile(string Path, string Sha256, long Bytes);

/// <summary>Verify the exact reviewed SDK associated with a selected Blender installation; never discover new expectations.</summary>
public static class BundledGltfSdk
{
    public static void Verify(string blender, string expectedExecutableSha256, IReadOnlyList<BundledGltfFile> expected, CancellationToken cancellation = default)
    {
        if (!Path.IsPathFullyQualified(blender) || !Regex.IsMatch(expectedExecutableSha256,"^[a-f0-9]{64}$") || expected.Count is < 1 or > 4096)
            throw new InvalidOperationException("Explicit bounded Blender/glTF toolchain identity required");
        blender=Path.GetFullPath(blender);var installation=Path.GetDirectoryName(blender)!;
        var root=Path.Combine(installation,"4.5","scripts","addons_core","io_scene_gltf2");
        NoLinks(root,installation);
        if(Hash(blender,1_073_741_824,cancellation).Sha256!=expectedExecutableSha256)throw new InvalidOperationException("Selected mechanical Blender executable changed");
        var comparer=OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
        var declared=new Dictionary<string,BundledGltfFile>(comparer);long total=0;
        foreach(var file in expected)
        {
            if(string.IsNullOrWhiteSpace(file.Path)||file.Path.Any(char.IsControl)||file.Path.Contains('\\')||file.Path.Contains(':')||file.Path.StartsWith('/')||file.Path.Split('/').Any(part=>part is "" or "." or "..")||!Regex.IsMatch(file.Sha256,"^[a-f0-9]{64}$")||file.Bytes is <0 or >16_777_216||file.Bytes>134_217_728-total||!declared.TryAdd(file.Path,file))
                throw new InvalidOperationException("Invalid immutable mechanical glTF inventory");
            total+=file.Bytes;
        }
        if(!declared.ContainsKey("__init__.py"))throw new InvalidOperationException("Mechanical glTF inventory lacks its package root");
        var queue=new Queue<string>();queue.Enqueue(root);var observed=new HashSet<string>(comparer);var directories=1;
        while(queue.TryDequeue(out var parent))foreach(var path in Directory.EnumerateFileSystemEntries(parent))
        {
            cancellation.ThrowIfCancellationRequested();var attributes=File.GetAttributes(path);
            if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Linked mechanical glTF entries are unsupported");
            if((attributes&FileAttributes.Directory)!=0){if(++directories>1024)throw new InvalidOperationException("Mechanical glTF directory budget exceeded");queue.Enqueue(path);continue;}
            var relative=Path.GetRelativePath(root,path).Replace('\\','/');
            if(!observed.Add(relative)||observed.Count>4096||!declared.TryGetValue(relative,out var expectedFile))throw new InvalidOperationException("Mechanical glTF inventory has unexpected files");
            var actual=Hash(path,16_777_216,cancellation);
            if(actual.Bytes!=expectedFile.Bytes||actual.Sha256!=expectedFile.Sha256)throw new InvalidOperationException("Mechanical glTF SDK changed: "+relative);
        }
        if(!observed.SetEquals(declared.Keys))throw new InvalidOperationException("Mechanical glTF SDK is incomplete");
        NoLinks(root,installation);cancellation.ThrowIfCancellationRequested();
    }
    static void NoLinks(string path,string stop)
    {
        while(true)
        {
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Linked selected Blender/glTF installation is unsupported");
            if(path.Equals(stop,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))return;
            path=Path.GetDirectoryName(path)??throw new InvalidOperationException("Selected glTF installation escaped its Blender root");
        }
    }
    static (string Sha256,long Bytes) Hash(string path,long maximum,CancellationToken ct)
    {
        var info=new FileInfo(path);
        if(!info.Exists||(info.Attributes&FileAttributes.ReparsePoint)!=0||info.Length<0||info.Length>maximum)throw new InvalidOperationException("Missing, linked or oversized mechanical toolchain file");
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes=new byte[65536];long total=0;
        while(true){ct.ThrowIfCancellationRequested();var count=file.Read(bytes);if(count==0)break;total+=count;if(total>maximum)throw new InvalidOperationException("Mechanical SDK file grew beyond bounds");hash.AppendData(bytes,0,count);}
        if(total!=info.Length)throw new InvalidOperationException("Mechanical SDK file changed while hashing");
        return(Convert.ToHexStringLower(hash.GetHashAndReset()),total);
    }
}
