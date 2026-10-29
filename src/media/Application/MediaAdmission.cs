using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Bubo.Media.Domain;
using OpenCaw.AssetProduction;
using HostExecutionContext = Bubo.Host.HostExecutionContext;

namespace Bubo.Media.Application;

public sealed record AdmittedMediaRequest(MediaRequest Request, BackendCapability Capability, string RequestPath, string RequestSha256,
    string StateDirectory, string OutputDirectory, string RightsSha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MediaRights(string SchemaVersion, string Task, string Owner, bool PromptRightsConfirmed, bool GenerationAllowed);

/// <summary>Fresh policy, rights and path admission, before output or budget mutation.</summary>
public sealed class MediaAdmission(IWorkspace workspace)
{
    public HostExecutionContext Context => workspace.HostContext ?? throw new InvalidOperationException("Explicit validated host context is required");

    public AdmittedMediaRequest Read(string path)
    {
        var context=Context;
        context.ValidateFresh();
        if (!context.MediaOnly || context.InspectionOnly || context.Pipeline!="LOCAL" || context.MediaPath is null || context.MediaSha256 is null || context.AllowPromotion)
            throw new InvalidOperationException("Media requires explicit hash-bound LOCAL media policy; no provider fallback");
        context.RequireReference(path,"input");
        var request=MediaJson.Read<MediaRequest>(ReadBounded(context.ResolveReadPath(path)));
        var capability=MediaCapabilities.Require(request.Backend);
        request.Validate(capability);
        if (request.Task!=context.TaskId) throw new InvalidOperationException("Media request belongs to another task");
        if (capability.Modality=="image" && (context.StyleSha256.Length==0
            || !context.References.Any(x=>x.Kind=="policy" && x.Sha256==context.StyleSha256)))
            throw new InvalidOperationException("Image execution requires its actual registered host style selection");
        Reference(request.Rights,"rights");
        Reference(request.Installation,"input");
        var rights=MediaJson.Read<MediaRights>(ReadBounded(context.ResolveReadPath(request.Rights.Path)));
        if (rights.SchemaVersion!="bubo-media-rights/v1" || rights.Task!=context.TaskId || string.IsNullOrWhiteSpace(rights.Owner)
            || rights.Owner.Length>160 || !rights.PromptRightsConfirmed || !rights.GenerationAllowed)
            throw new InvalidOperationException("Current scoped generation and prompt-rights evidence is required");
        if (request.Acceptance is { } acceptance)
        {
            if (!context.HasExplicitMediaStateRoot) throw new InvalidOperationException("Acceptance requires its explicitly shared six-case state root");
            Reference(acceptance.Approval,"policy");
        }
        var state=context.StateRoot+"/media/"+request.RequestId;
        var output=request.OutputDirectory.TrimEnd('/','\\')+"/"+request.RequestId;
        var fullOutput=context.ResolveWritePath(output);
        var staging=context.ResolveWritePath(context.StagingRoot);
        if (!Within(fullOutput,staging) || fullOutput.Equals(staging,Comparison))
            throw new InvalidOperationException("Generation output must stay in a fresh non-runtime staging child");
        if (Within(fullOutput,context.ResolveReadPath(request.Installation.Path)) || Within(context.ResolveReadPath(request.Installation.Path),fullOutput))
            throw new InvalidOperationException("Output cannot overlap the installation receipt");
        var fullState=context.ResolveWritePath(state);
        if (Within(fullOutput,fullState) || Within(fullState,fullOutput))
            throw new InvalidOperationException("Output and execution state must have separate ownership");
        if (File.Exists(fullState) || Directory.Exists(fullState) || File.Exists(fullOutput) || Directory.Exists(fullOutput))
            throw new InvalidOperationException("Request identity/output already exists; immutable attempts cannot be overwritten");
        return new(request,capability,workspace.Relative(path),workspace.HashProjectFile(path),workspace.Relative(fullState),workspace.Relative(fullOutput),request.Rights.Sha256);
    }

    public void Reference(FileIdentity file,string kind)
    {
        file.Validate();Context.RequireReference(file.Path,kind);
        if (workspace.HashProjectFile(file.Path)!=file.Sha256)
            throw new InvalidOperationException("Request reference hash differs from actual registered evidence");
    }

    public void RequireInstallationSeparation(PreparedInstallation installation,params string[] mutablePaths)
    {
        var immutable=(installation.ImmutableRoots??[installation.ModelDirectory]).Append(installation.Worker).Append(installation.ReceiptPath)
            .Select(path=>Path.GetFullPath(path,Context.Root)).ToArray();
        foreach(var path in mutablePaths)
        {
            var full=Context.ResolveWritePath(path);
            if(immutable.Any(root=>Within(full,root)||Within(root,full)))
                throw new InvalidOperationException("Media state, cache, ledger and output cannot overlap immutable installation roots");
        }
    }

    public static byte[] ReadBounded(string file)
    {
        using var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
        if (input.Length is <2 or >1048576) throw new InvalidOperationException("Media document must contain 2bytes..1MiB");
        var bytes=new byte[checked((int)input.Length)];input.ReadExactly(bytes);
        if (input.ReadByte()!=-1) throw new InvalidOperationException("Media document changed while being read");
        return bytes;
    }
    public static string Sha256(string file)
    {
        using var stream=File.OpenRead(file);return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    static StringComparison Comparison => OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
    public static bool Within(string path,string parent)
    {
        path=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        parent=Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        return path.Equals(parent,Comparison) || path.StartsWith(parent+Path.DirectorySeparatorChar,Comparison);
    }
}
