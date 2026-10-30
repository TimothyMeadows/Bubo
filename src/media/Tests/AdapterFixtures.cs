using System.Text;
using System.Text.Json;
using Bubo.Media.Application;
using Bubo.Media.Domain;
using Bubo.Media.Infrastructure;
using OpenCaw.AssetProduction;

static class AdapterFixtures
{
    public static void Run(Workspace workspace,MediaRequest original,PreparedInstallation installation,Action<bool,string> check)
    {
        var request=original with {Items=[original.Items[0] with {Id="CON",Width=64,Height=64},original.Items[0] with {Id="con",Width=64,Height=64}],Budget=original.Budget with {MaximumOutputs=2,MaximumTotalBytes=2097152}};
        request.Validate(MediaCapabilities.Require("flux-schnell"));
        var output=workspace.ResolveWrite(request.OutputDirectory+"/"+request.RequestId);var native=Path.Combine(output,"native");Directory.CreateDirectory(native);
        var response=workspace.ResolveWrite(workspace.HostContext!.StateRoot+"/native-response-fixture.bin");Directory.CreateDirectory(Path.GetDirectoryName(response)!);
        void Text(BinaryWriter writer,string value){var bytes=Encoding.UTF8.GetBytes(value);writer.Write((uint)bytes.Length);writer.Write(bytes);}
        using(var file=new FileStream(response,FileMode.CreateNew))using(var writer=new BinaryWriter(file))
        {
            writer.Write(Encoding.ASCII.GetBytes("BFLXR001"));writer.Write(2u);writer.Write(.1);writer.Write(.1);
            Text(writer,"SIMULATED binary response; no GPU");Text(writer,new string('0',32));Text(writer,"SIMULATED model; no weights");writer.Write(12u);writer.Write(0u);Text(writer,"77b72a2");
            writer.Write(OperatingSystem.IsWindows()?1u:0u);if(OperatingSystem.IsWindows())Text(writer,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"kernel32.dll"));
            for(var i=0;i<2;i++)
            {
                var item=request.Items[i];File.WriteAllBytes(Path.Combine(native,$"{i:000}.rgb"),new byte[64*64*3]);
                File.WriteAllText(Path.Combine(native,$"{i:000}.info.json"),JsonSerializer.Serialize(new {schema="sdcpp.generation/v1",kind="image",prompt=item.Prompt,negative_prompt="",width=64,height=64,seed=item.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture),sample_params=new{sample_steps=item.Steps,guidance=new{txt_cfg=item.Guidance},sample_method="euler",scheduler="simple"}}));
                Text(writer,item.Id);Text(writer,$"{i:000}.rgb");Text(writer,$"{i:000}.info.json");Text(writer,"simple");writer.Write(.1);writer.Write(64u);writer.Write(64u);writer.Write(3u);writer.Write(item.Seed);writer.Write((uint)item.Steps!.Value);
            }
        }
        var result=new FixedMediaWorker(workspace,new NoNative()).ReadImage(request,installation,response,native,output,false);
        check(result.Outputs.Select(x=>Path.GetFileName(x.Path)).SequenceEqual(new[]{"000.png","001.png"})&&result.Outputs.Select(x=>x.Id).SequenceEqual(new[]{"CON","con"}),"SIMULATED native adapter preserves reserved/case-distinct IDs through unique numbered PNG paths");
        check(result.Outputs.All(x=>File.Exists(workspace.Resolve(x.Path))&&x.Seed==request.Items[0].Seed&&x.NativeMetadata is not null&&workspace.HashProjectFile(x.NativeMetadata.Path)==x.NativeMetadata.Sha256),"SIMULATED native response decoding retains actual seed and hash-bound metadata without model execution");
    }
    sealed class NoNative:INativeProcess
    {
        public Task Run(string executable,IEnumerable<string> arguments,string cwd,string log,int seconds,CancellationToken cancellation=default,long maximumLogBytes=16777216,IReadOnlyDictionary<string,string>? environment=null)=>throw new InvalidOperationException("Fixture forbids process launches");
    }
}
