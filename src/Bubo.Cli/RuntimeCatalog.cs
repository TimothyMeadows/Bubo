using System.Text.Json.Nodes;
using OpenCaw.AssetProduction;

static class RuntimeCatalog
{
    public const string ExpectedSha256 = "01e986c1ed04717ef184ee594bef41ce2d41e4cd4f9eb8ebe571d678ed7206ba";
    public static void Verify()
    {
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        var workspace = new Workspace(root);
        var file = workspace.Resolve("resources/catalog.json");
        if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024 || Workspace.Hash(file) != ExpectedSha256)
            throw new InvalidOperationException("Bubo audited resource catalog is missing or modified; rebuild from the pinned source");
        var catalog = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        if (catalog["schemaVersion"]?.GetValue<int>() != 1 || catalog["files"] is not JsonObject files || files.Count == 0)
            throw new InvalidOperationException("Invalid Bubo audited resource catalog");
        foreach (var (relative,hash) in files)
        {
            if (!relative.StartsWith("resources/",StringComparison.Ordinal) || relative.Split('/').Any(part => part is ".." or "." or ""))
                throw new InvalidOperationException("Invalid audited resource path");
            var path = workspace.Resolve(relative);
            if (!File.Exists(path) || Workspace.Hash(path) != hash!.GetValue<string>())
                throw new InvalidOperationException("Bubo audited resource is missing or changed: " + relative);
        }
    }
}
