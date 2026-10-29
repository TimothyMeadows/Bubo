using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bubo.Host;
using HostExecutionContext = Bubo.Host.HostExecutionContext;

// TEST-ONLY HOST ADAPTER: models the old host's fixture policy decisions so the
// extracted execution regressions keep their original inputs/assertions. This
// class is compiled only into RuntimeTests, never into a Bubo runtime assembly.
// Production Bubo accepts explicit host JSON and does not interpret documents.
class Workspace(string root) : OpenCaw.AssetProduction.Workspace(root), OpenCaw.ProofToModel.Application.IWorkspace
{
    // Host adaptation precedes fixture snapshots, matching caller preparation.
    public new string HashFile(string path) { _ = HostContext; return base.HashFile(path); }
    public new string HashProjectFile(string path) { _ = HostContext; return base.HashProjectFile(path); }
    public override HostExecutionContext? HostContext
    {
        get
        {
            var stylePath=Path.Combine(Root,"STYLE.md");
            if (!File.Exists(stylePath)) return null;
            var style=File.ReadAllText(stylePath);
            var styleHash=Hash(stylePath);
            var pipeline=Regex.Match(style,@"Primary OpenCaw art pipeline:\s*\r?\n- ([A-Z_]+)").Groups[1].Value;
            if (string.IsNullOrEmpty(pipeline)) pipeline="PROOF_TO_MODEL";
            var references=new JsonArray();
            foreach (var file in Directory.GetFiles(Root).Where(path=>Path.GetFileName(path) is "STYLE.md" or "MEDIA.md" or "selection.json"))
                references.Add(new JsonObject { ["path"]=Path.GetFileName(file), ["sha256"]=Hash(file), ["kind"]=Path.GetFileName(file) is "STYLE.md" or "MEDIA.md" ? "policy" : "input" });
            var selection=Path.Combine(Root,"selection.json");
            if (File.Exists(selection))
            {
                var selected=JsonNode.Parse(File.ReadAllText(selection))!.AsObject();
                if (selected["pipeline"]?.GetValue<string>()=="PROOF_TO_MODEL" && (selected["styleSha256"]?.GetValue<string>()??selected["styleContractHash"]?.GetValue<string>())==styleHash)
                    pipeline="PROOF_TO_MODEL";
            }
            var libraries=new JsonArray();
            var section=Regex.Match(style,@"(?ms)^## External Asset Libraries\s*\r?\n(?<body>.*?)(?=^## |\z)").Groups["body"].Value;
            foreach (Match item in Regex.Matches(section,@"(?m)^- (?<id>[a-z0-9][a-z0-9-]*): `(?<root>[^`]+)`\s*$"))
                libraries.Add(new JsonObject{["id"]=item.Groups["id"].Value,["root"]=item.Groups["root"].Value});
            var policy=new JsonObject{["pipeline"]=pipeline,["styleSha256"]=styleHash,["engine"]=new JsonObject{["effectiveEngine"]="THREEJS"},["libraries"]=libraries};
            var media=Path.Combine(Root,"MEDIA.md");
            if (File.Exists(media)) policy["media"]=new JsonObject{["path"]="MEDIA.md",["sha256"]=Hash(media)};
            var context=new JsonObject
            {
                ["schemaVersion"]="bubo-host-context/v1",["projectRoot"]=Root,["task"]="fixture",
                ["roots"]=new JsonObject{["state"]=".opencaw/.cache/tasks",["staging"]=".art-staging",["tools"]=new JsonArray(".local/tools"),["inputs"]=new JsonArray("inputs"),["outputs"]=new JsonArray("assets/models")},
                ["policy"]=policy,["references"]=references,["tools"]=new JsonArray(),
                ["budgets"]=new JsonObject{["timeoutSeconds"]=1800,["maxLogBytes"]=16777216},
                ["permissions"]=new JsonObject{["provisioning"]=false,["promotion"]=false}
            };
            var path=Path.Combine(Root,".fixture-host-context.json");
            var serialized=context.ToJsonString();
            if (!File.Exists(path) || File.ReadAllText(path)!=serialized) File.WriteAllText(path,serialized);
            return HostExecutionContext.Load(Root,path);
        }
    }
}
