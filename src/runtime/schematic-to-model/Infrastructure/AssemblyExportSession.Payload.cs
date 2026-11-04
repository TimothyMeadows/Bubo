using System.Security.Cryptography;
using System.Text.Json;
using Bubo.Schematic.Domain;

namespace Bubo.Schematic.Infrastructure;

public sealed partial class AssemblyExportSession
{
    void WriteJson<T>(string path,T value,CancellationToken ct,long maximum=JsonLimit)
    {
        if(maximum<2||maximum>JsonLimit)throw new InvalidOperationException("No remaining bounded export JSON allowance");
        ct.ThrowIfCancellationRequested();var output=workspace.ResolveWrite(path);Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        using(var bounded=new JsonWriter(file,ct,maximum))JsonSerializer.Serialize(bounded,value,Compact);
        file.Flush(true);ct.ThrowIfCancellationRequested();
    }
    sealed class JsonWriter(Stream inner,CancellationToken ct,long maximum):Stream
    {
        long written;
        public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>written;public override long Position{get=>written;set=>throw new NotSupportedException();}
        public override void Flush()=>inner.Flush();
        public override void Write(byte[] bytes,int offset,int count)=>Write(bytes.AsSpan(offset,count));
        public override void Write(ReadOnlySpan<byte> bytes){ct.ThrowIfCancellationRequested();if(bytes.Length>maximum-written)throw new InvalidOperationException("Complete export JSON exceeds its remaining finite byte ceiling");inner.Write(bytes);written+=bytes.Length;}
        public override int Read(byte[] bytes,int offset,int count)=>throw new NotSupportedException();public override long Seek(long n,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();
    }
    AssemblyExportFile Copy(string source,string destination,string hash,long maximum,CancellationToken ct)
    {
        var input=workspace.Resolve(source);var output=workspace.ResolveWrite(destination);
        if(input.Equals(output,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new InvalidOperationException("Export copy overlaps its immutable source");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var reader=new FileStream(input,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(reader.Length<=0||reader.Length>maximum||(File.GetAttributes(input)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Linked, empty or excessive export source");
        var length=reader.Length;using var writer=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];long count=0;
        while(true){ct.ThrowIfCancellationRequested();var n=reader.Read(buffer);if(n==0)break;if(n>maximum-count)throw new InvalidOperationException("Export source grew beyond budget");digest.AppendData(buffer,0,n);writer.Write(buffer,0,n);count+=n;}
        if(count!=length||Convert.ToHexStringLower(digest.GetHashAndReset())!=hash)throw new InvalidOperationException("Export source changed while copying");
        writer.Flush(true);ct.ThrowIfCancellationRequested();return new(destination,hash,count);
    }
    SortedDictionary<string,long> Inventory(string directory,long maximum,CancellationToken ct)
    {
        var root=workspace.Resolve(directory);var result=new SortedDictionary<string,long>(StringComparer.Ordinal);
        var aliases=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var queue=new Queue<string>();queue.Enqueue(root);int entries=0;long total=0;
        while(queue.TryDequeue(out var parent))foreach(var entry in Directory.EnumerateFileSystemEntries(parent))
        {
            ct.ThrowIfCancellationRequested();if(++entries>32768)throw new InvalidOperationException("Export directory inventory exceeds finite entry ceiling");
            var relative=Path.GetRelativePath(root,workspace.Resolve(entry)).Replace('\\','/');var attrs=File.GetAttributes(entry);
            if(!ExportPackageValidator.IsPortablePath(relative)||!aliases.Add(relative)||(attrs&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Ambiguous or linked export directory entry");
            if((attrs&FileAttributes.Directory)!=0){queue.Enqueue(entry);continue;}
            var length=new FileInfo(entry).Length;if(length<0||length>maximum-total)throw new InvalidOperationException("Cumulative schematic export bytes exceeded");
            total+=length;result.Add(relative,length);if(result.Count>16384)throw new InvalidOperationException("Cumulative schematic export file ceiling exceeded");
        }
        return result;
    }
    long RemainingBytes(SourceSnapshot source,Attempt attempt,CancellationToken ct)
    {
        var maximum=Limits(source).MaxOutputBytes;
        return Directory.Exists(workspace.Resolve(attempt.Directory))?maximum-Inventory(attempt.Directory,maximum,ct).Values.Sum():maximum;
    }
    Payload Seal(SourceSnapshot source,Attempt attempt,AssemblyExportReport producer,CancellationToken ct)
    {
        var input=source.Source.Parts.Input;var spec=input.Design;var directory=attempt.Directory+"/package";
        if(Directory.Exists(workspace.Resolve(directory)))throw new InvalidOperationException("Portable payload already exists");
        var maximum=RemainingBytes(source,attempt,ct);
        if(Inventory(attempt.Directory,Limits(source).MaxOutputBytes,ct).Count+input.Package!.Artifacts.Count+19>16_384)
            throw new InvalidOperationException("Complete sealed payload cannot fit the remaining cumulative file allowance");
        Directory.CreateDirectory(workspace.ResolveWrite(directory));long copied=0;var files=new List<AssemblyExportFile>();
        void AddCopy(string from,string to,string hash)
        {var f=Copy(from,directory+"/"+to,hash,maximum-copied,ct);copied=checked(copied+f.Bytes);files.Add(f with{Path=to});}
        void AddJson<T>(string to,T value)
        {WriteJson(directory+"/"+to,value,ct,Math.Min(JsonLimit,maximum-copied));var f=Describe(directory+"/"+to,Math.Min(JsonLimit,maximum-copied),ct);copied=checked(copied+f.Bytes);files.Add(f with{Path=to});}
        AddCopy(source.Source.Assembly.Scene,"assembly.blend",attempt.Identity.AssemblySourceSha256);
        AddCopy(source.Source.Motion.Scene,"diagnostic-motion/diagnostic.blend",attempt.Identity.MotionSourceSha256);
        AddCopy(input.Manifest.OriginalDesign,"assembly-design.json",input.Manifest.DesignSha256);
        foreach(var (output,target) in new[]{("assembly.glb","assembly.glb"),("diagnostic.glb","diagnostic-motion/diagnostic.glb")})
        {var f=producer.Outputs.Single(f=>f.Path==output);AddCopy(attempt.Directory+"/produce/output/"+output,target,f.Sha256);}
        var preview=input.Manifest.Previews[^1];var approval=input.Manifest.Reviews[^1];
        foreach(var (name,hash) in input.Package!.Artifacts)
            AddCopy(preview.Directory+"/"+name,"drawings/"+name,hash);
        AddCopy(preview.Directory+"/bom.json","bom.json",input.Package.Artifacts["bom.json"]);
        AddCopy(approval.Snapshot,"provenance/design-approval.json",approval.Sha256);
        AddCopy(source.Inputs.AssemblyJob.Path,"provenance/assembly-job.json",source.Inputs.AssemblyJob.Sha256);
        AddCopy(source.Inputs.AssemblyReport.Path,"provenance/assembly-report.json",source.Inputs.AssemblyReport.Sha256);
        AddCopy(source.Inputs.AssemblyVerification.Path,"provenance/assembly-native-verification.json",source.Inputs.AssemblyVerification.Sha256);
        AddCopy(source.Inputs.MotionJob.Path,"provenance/motion-job.json",source.Inputs.MotionJob.Sha256);
        AddCopy(source.Inputs.MotionReport.Path,"provenance/motion-report.json",source.Inputs.MotionReport.Sha256);
        AddCopy(source.Inputs.MotionVerification.Path,"provenance/motion-native-verification.json",source.Inputs.MotionVerification.Sha256);
        AddCopy(workspace.Relative(Path.Combine(Path.GetDirectoryName(workspace.Resolve(source.Source.Parts.Directory))!,"job.json")),"provenance/parts-job.json",source.Source.Parts.JobSha256);
        AddCopy(source.Source.Parts.Directory+"/parts-report.json","provenance/parts-report.json",source.Source.Parts.ReportSha256);
        AddCopy(source.Source.Parts.Directory+"/parts-verification.json","provenance/parts-verification.json",source.Source.Parts.VerificationSha256);
        var components=spec.Instances.Select((i,index)=>new AssemblyExportComponent(i.Id,i.PartId,"bubo.instance."+index.ToString("D6",System.Globalization.CultureInfo.InvariantCulture),
            spec.Parts.Single(p=>p.Id==i.PartId).Kind==PartKind.Rigid?"rigid":"flexible",spec.Parts.Single(p=>p.Id==i.PartId).Kind==PartKind.Rigid?null:"bubo.rig."+index.ToString("D6",System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var coordinates=new AssemblyExportCoordinates("right-handed-metres-z-up","gltf-right-handed-metres-y-up",new double[]{1,0,0,0,0,0,-1,0,0,1,0,0,0,0,0,1});
        AddJson("assembly-metadata.json",new{schemaVersion="bubo-mechanical-assembly-metadata/v1",ancestry=attempt.Identity,coordinates,components,
            spec.Parts,spec.Instances,spec.Subassemblies,spec.Joints,spec.Mechanisms,spec.Routes,spec.Connections,spec.Materials,spec.Numerics,spec.Observations,
            rest=source.Source.Assembly.Native.Report.Instances,diagnosticMotionIsReadinessOnly=true,continuousCollisionCertified=false,manufacturingCertified=false});
        AddJson("diagnostic-motion/diagnostic-motion.json",new{schemaVersion="bubo-schematic-diagnostic-package/v1",ancestry=attempt.Identity,
            plan=source.Source.Motion.Job.Plan,rigs=source.Source.Motion.Job.Rigs,clips=source.Source.Motion.Native.Report.Clips.Select((clip,index)=>new{clip.Id,
                portableAnimationName="bubo.clip."+index.ToString("D6",System.Globalization.CultureInfo.InvariantCulture),samples=clip.Samples.Select(s=>new{s.Index,s.TimeSeconds})}),
            diagnosticOnly=true,finishedAnimation=false,humanMotionAccepted=false,runtimeAccepted=false});
        var package=new AssemblyExportPackage{SchemaVersion="bubo-schematic-export-package/v1",Task=input.Manifest.Task,Asset=input.Manifest.Asset,Version=input.Manifest.Version,
            ModelAuthor=attempt.Author,Ancestry=attempt.Identity,Coordinates=coordinates,Numerics=spec.Numerics,Components=components,Files=files.OrderBy(f=>f.Path,StringComparer.Ordinal).ToArray(),
            Bundle=attempt.Bundle,Toolchain=new(attempt.Toolchain.BlenderSha256,producer.BlenderVersion,attempt.Toolchain.Files)};
        ExportPackageValidator.RequireValid(package,spec);WriteJson(directory+"/package.json",package,ct,Math.Min(JsonLimit,maximum-copied));var packageHash=PartOutputVerifier.FileHash(workspace,directory+"/package.json",ct,JsonLimit);
        attempt.PackageSha256=packageHash;return InspectPayload(source,attempt,ct);
    }
    Payload InspectPayload(SourceSnapshot source,Attempt attempt,CancellationToken ct)
    {
        var directory=attempt.Directory+"/package";var manifest=directory+"/package.json";
        if(PartOutputVerifier.FileHash(workspace,manifest,ct,JsonLimit)!=attempt.PackageSha256)throw new InvalidOperationException("Sealed schematic package changed");
        var package=StrictDesignJson.Read<AssemblyExportPackage>(workspace.Resolve(manifest));ExportPackageValidator.RequireValid(package,source.Source.Parts.Input.Design);
        var input=source.Source.Parts.Input;
        if(package.Task!=input.Manifest.Task||package.Asset!=input.Manifest.Asset||package.Version!=input.Manifest.Version||package.Ancestry!=attempt.Identity||package.ModelAuthor!=attempt.Author
            ||Digest(package.Bundle)!=Digest(attempt.Bundle)||!package.Toolchain.GltfFiles.SequenceEqual(attempt.Toolchain.Files))throw new InvalidOperationException("Package ancestry, author or toolchain differs");
        var inventory=Inventory(directory,Limits(source).MaxOutputBytes,ct);
        if(!inventory.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(package.Files.Select(f=>f.Path).Append("package.json")))throw new InvalidOperationException("Sealed package contains missing or unexpected files");
        foreach(var file in package.Files)
            if(inventory[file.Path]!=file.Bytes||PartOutputVerifier.FileHash(workspace,directory+"/"+file.Path,ct,file.Bytes)!=file.Sha256)throw new InvalidOperationException("Sealed payload changed: "+file.Path);
        if(PartOutputVerifier.FileHash(workspace,manifest,ct,JsonLimit)!=attempt.PackageSha256||!Inventory(directory,Limits(source).MaxOutputBytes,ct).SequenceEqual(inventory))
            throw new InvalidOperationException("Package changed during inspection");
        return new(package,directory,manifest,attempt.PackageSha256!);
    }
}
