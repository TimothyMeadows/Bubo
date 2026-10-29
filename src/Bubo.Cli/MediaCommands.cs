using System.Text.Json.Nodes;
using Bubo.Host;
using Bubo.Media.Application;
using Bubo.Media.Infrastructure;
using OpenCaw.AssetProduction;
using HostExecutionContext=Bubo.Host.HostExecutionContext;

internal static class MediaCommands
{
    public static async Task<object> Execute(HostExecutionContext host,string operation,Dictionary<string,string> options,JsonObject build)
    {
        var manifest=options.GetValueOrDefault("manifest")??throw new InvalidOperationException("Media requires a registered --manifest request");
        host.RequireReference(manifest,"input");
        if(options.TryGetValue("load",out var load) && load!="true")throw new InvalidOperationException("--load accepts only true; omit for file-only inspection");
        var workspace=new Workspace(host.Root,host);
        var resources=new AuditedResources(AppContext.BaseDirectory,RuntimeCatalog.ExpectedSha256);
        var process=new HostNativeProcess(host,new NativeProcess());
        var verifier=new InstallationVerifier(workspace,resources);
        var worker=new FixedMediaWorker(workspace,process);
        var service=new MediaService(workspace,verifier,worker,build);
        using var cancellation=new CancellationTokenSource();
        ConsoleCancelEventHandler cancel=(_,e)=>{e.Cancel=true;cancellation.Cancel();};
        Console.CancelKeyPress+=cancel;
        try
        {
            object data=operation switch
            {
                "inspect" when options.ContainsKey("load")=>await service.Probe(manifest,(r,i,p,c)=>worker.Probe(r,i,p,c),cancellation.Token),
                "inspect"=>service.Inspect(manifest),
                "generate"=>await service.Generate(manifest,cancellation.Token),
                "setup" when JsonNode.Parse(MediaAdmission.ReadBounded(host.ResolveReadPath(manifest)))?["schemaVersion"]?.GetValue<string>()=="bubo-media-provision/v1"
                    =>await new MediaProvision(workspace,resources,process).Execute(manifest,options.ContainsKey("execute"),cancellation.Token),
                "setup"=>await new MediaSetup(workspace,resources).Execute(manifest,options.ContainsKey("execute"),cancellation.Token),
                _=>throw new InvalidOperationException("Unsupported media operation")
            };
            return new {schemaVersion="bubo-execution-result/v1",apiVersion=1,status="succeeded",workflow="media",operation,hostContextSha256=host.ContextSha256,runtime=build,data};
        }
        finally {Console.CancelKeyPress-=cancel;}
    }
}
