using System.Text.Json;
using Bubo.Schematic.Domain;
using Bubo.Schematic.Infrastructure;
using Hephaera.AssetProduction;

namespace Bubo.Schematic.Tests;

public static class ExportSessionTests
{
    public static void Run()
    {
        Check.Case("export state bounds the actual persisted bytes, retaining a terminal reserve",()=>
        {
            var files=Enumerable.Range(0,4096).Select(i=>new AssemblyExportToolchainFile($"module{i:D4}/"+new string('p',170)+".py",new('a',64),1)).ToArray();
            var tool=new AssemblyExportToolchain("C:/fixture/blender.exe",new('a',64),"C:/fixture/io_scene_gltf2",files,new('a',64));
            var state=new{schemaVersion="bubo-schematic-export-state/v1",task="qa",asset="fixture",version="v1",manifest="state/schematic-to-model/fixture/v1/manifest.json",runtimeSha256=new string('a',64),
                attempts=Enumerable.Range(0,6).Select(i=>new{operation="export",directory="staging/attempts/export-"+i.ToString("x32"),author="qa",
                    identity=ExportContractTests.Identity(),bundle=new SortedDictionary<string,string>{{"export_author.py",new('a',64)}},toolchain=tool,
                    startedAt=DateTimeOffset.Parse("2026-11-04T00:00:00Z"),exportDirectory=(string?)null,packageSha256=(string?)null,status="running",error=(string?)null,
                    completedAt=(DateTimeOffset?)null,runs=new SortedDictionary<string,string>()}).ToArray(),decisions=Array.Empty<string>()};
            var compact=JsonSerializer.SerializeToUtf8Bytes(state,new JsonSerializerOptions(Workspace.Json){WriteIndented=false}).Length;
            var persisted=JsonSerializer.SerializeToUtf8Bytes(state,Workspace.Json).Length;
            Check.True(compact<StrictDesignJson.MaximumInputBytes-65_536,"Fixture must pass the formerly incorrect compact-byte check");
            Check.True(persisted>StrictDesignJson.MaximumInputBytes,"Actual persisted state would exceed its read ceiling");
            Check.Throws<InvalidOperationException>(()=>AssemblyExportSession.RequireStateSize(state));
            AssemblyExportSession.RequireStateSize(new{schemaVersion="small-state",attempts=Array.Empty<string>()});
        });
    }
}
