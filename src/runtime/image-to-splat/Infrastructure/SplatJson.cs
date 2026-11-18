using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hephaera.AssetProduction;

namespace Bubo.Splats.Infrastructure;

/// <summary>Strict bounded descriptors, binding the bytes actually parsed to caller identities.</summary>
public static class SplatJson
{
    public static JsonSerializerOptions Options => new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow, MaxDepth=32, WriteIndented=true };
    public static T Read<T>(Workspace workspace,string path,string kind="input",int maximum=65536)
    {
        var context=workspace.HostContext??throw new InvalidOperationException("Host context required");
        context.RequireReference(path,kind);
        var relative=workspace.Relative(path);
        var reference=context.References.Single(x=>x.Kind==kind && string.Equals(x.Path,relative,
            OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal));
        return ReadIdentity<T>(workspace.Resolve(path),reference.Sha256,maximum);
    }
    public static T ReadIdentity<T>(string full,string sha256,int maximum=65536)
    {
        var bytes=ReadBytes(full,maximum);
        if(Convert.ToHexStringLower(SHA256.HashData(bytes))!=sha256)throw new InvalidOperationException("Parsed descriptor bytes differ from admitted identity");
        return Parse<T>(bytes);
    }
    public static T ReadFile<T>(string full,int maximum=8388608)=>Parse<T>(ReadBytes(full,maximum));
    public static byte[] ReadBytes(string full,int maximum)
    {
        if((File.GetAttributes(full)&(FileAttributes.ReparsePoint|FileAttributes.Directory|FileAttributes.Device))!=0)
            throw new InvalidOperationException("Regular unlinked descriptor required");
        using var file=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length<2 || file.Length>maximum)throw new InvalidOperationException("Descriptor exceeds its finite byte bound");
        var bytes=new byte[checked((int)file.Length)];file.ReadExactly(bytes);
        if(file.ReadByte()!=-1 || file.Length!=bytes.Length)throw new InvalidOperationException("Descriptor changed while reading");
        return bytes;
    }
    public static T Parse<T>(byte[] bytes)
    {
        using var parsed=JsonDocument.Parse(bytes,new(){MaxDepth=32});RejectDuplicates(parsed.RootElement);
        return parsed.RootElement.Deserialize<T>(Options)??throw new InvalidOperationException("Missing descriptor");
    }
    static void RejectDuplicates(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var field in value.EnumerateObject())
            { if(!names.Add(field.Name))throw new InvalidOperationException("Duplicate JSON property");RejectDuplicates(field.Value); }
        }
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())RejectDuplicates(item);
    }
    public static void WriteNew(Workspace workspace,string path,object value)
    {
        var full=workspace.ResolveWrite(path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        full=workspace.ResolveWrite(path);
        using var output=new FileStream(full,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        JsonSerializer.Serialize(output,value,value.GetType(),Options);output.Flush(true);
    }
    public static string Hash(object value)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value,value.GetType(),Options)));
}
