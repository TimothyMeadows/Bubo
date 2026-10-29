using System.Text.Json.Nodes;

namespace OpenCaw.AssetProduction;

/// <summary>Fresh access to the compiled caller's exact hash-bound resource bundle.</summary>
public sealed class AuditedResources(string root,string expectedCatalogSha256)
{
    public string CatalogSha256 { get; }=expectedCatalogSha256;
    readonly Workspace workspace=new(Path.GetFullPath(root));
    JsonObject Files()
    {
        var path=workspace.Resolve("resources/catalog.json");
        if(!File.Exists(path) || new FileInfo(path).Length>1048576 || Workspace.Hash(path)!=CatalogSha256)
            throw new InvalidOperationException("Audited resource catalog differs from this compiled runtime");
        var document=JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        if(document["schemaVersion"]?.GetValue<int>()!=1 || document["files"] is not JsonObject files || files.Count==0)
            throw new InvalidOperationException("Invalid audited resource catalog");
        return files;
    }
    public string Resolve(string relative)
    {
        if(!relative.StartsWith("resources/",StringComparison.Ordinal) || relative.Split('/').Any(p=>p is "" or "." or ".."))
            throw new InvalidOperationException("Invalid audited resource path");
        var files=Files();
        if(files[relative] is not JsonValue value || !value.TryGetValue<string>(out var expected))throw new InvalidOperationException("Resource is not in the compiled catalog");
        var path=workspace.Resolve(relative);
        if(!File.Exists(path) || Workspace.Hash(path)!=expected)throw new InvalidOperationException("Audited resource changed: "+relative);
        return path;
    }
    public string Hash(string relative)=>Workspace.Hash(Resolve(relative));
    public void Verify()
    {
        foreach(var (relative,_) in Files())_ = Resolve(relative);
    }
}
