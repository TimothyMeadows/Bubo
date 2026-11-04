using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Bubo.Vfx.Application;
using Bubo.Vfx.Infrastructure;
using Hephaera.AssetProduction;
using HostExecutionContext = Bubo.Host.HostExecutionContext;

namespace Bubo.Vfx.Tests;

internal static class InspectionTests
{
    internal static void Run(string root)
    {
        Fixture Make(string name) => new(Path.Combine(root, name));
        var good = Make("owned-positive");
        var before = good.Snapshot();
        var workspace = new ReadOnlyWorkspace(good.Workspace());
        var result = VfxInspection.Inspect(workspace, Fixture.PackagePath);
        Check.That(result.IsValid && result.ObservedResources.Count == 2, "actual local PNG and minimal owned GLB envelope inspect successfully");
        Check.That(result.PackageSha256 == good.Hash(Fixture.PackagePath) && result.PackageBytes == new FileInfo(good.Full(Fixture.PackagePath)).Length, "package digest and size bind actual current bytes");
        Check.That(result.ObservedResources.All(x => x.Sha256 == good.Hash(x.ProjectPath) && x.Bytes == new FileInfo(good.Full(x.ProjectPath)).Length), "every observed dependency binds current streamed hash and byte length");
        Check.That(result.PackagePath == Fixture.PackagePath && result.ObservedResources[0].Path == "textures/spark.png" && result.ObservedResources[0].ProjectPath == Fixture.TexturePath, "package-relative dependencies retain project-relative read identities");
        Check.That(result.Requirements.Engine.Id == "THREEJS" && result.Requirements.StyleSha256 == good.Hash("STYLE.md") && result.Requirements.LayerKinds.Contains("sprite"), "requirements describe exact declared host identities and layer kinds");
        Check.That(result.Capabilities.ContractValidationAvailable && result.Capabilities.DependencyInspectionAvailable
            && !result.Capabilities.ResourceContentValidationAvailable && !result.Capabilities.ResourcePreparationAvailable
            && !result.Capabilities.RuntimeRenderingAvailable && !result.Capabilities.ActionCompositionAvailable
            && !result.Capabilities.VideoDecodingAvailable && !result.Capabilities.MediaGenerationAvailable
            && !result.Capabilities.PromotionAvailable && result.Capabilities.ResourceContentStatus == "envelope-only-not-decoded", "capabilities distinguish contract inspection from unavailable production and content certification");
        Check.That(Equal(before, good.Snapshot()) && workspace.WriteCalls == 0, "successful inspection performs no filesystem writes or workspace mutation calls");
        var host = good.HostWorkspace();
        foreach (var operation in new[] { "validate", "inspect" })
            Check.That(VfxCommands.Execute(new ReadOnlyWorkspace(host), operation, Fixture.PackagePath).IsValid, "public " + operation + " admits an actual current registered full host context");
        Check.That(Equal(before, good.Snapshot()), "public validate and inspect create no state, output, locks, receipts or acceptance records");

        var missing = Make("missing-dependency"); File.Delete(missing.Full(Fixture.TexturePath));
        Reject(missing, "missing dependency rejects");
        var changed = Make("changed-dependency");
        var changedBytes = File.ReadAllBytes(changed.Full(Fixture.TexturePath)); changedBytes[^1] ^= 1; changed.Write(Fixture.TexturePath, changedBytes);
        Reject(changed, "same-size changed dependency hash rejects");
        var size = Make("wrong-size"); size.Package["resources"]![0]!["bytes"] = 1; size.SavePackage(); Reject(size, "declared dependency size differs from actual bytes");
        var extraBytes = Make("extra-bytes"); File.AppendAllText(extraBytes.Full(Fixture.TexturePath), "x"); Reject(extraBytes, "undeclared trailing dependency bytes reject");
        var packageLimit = Make("package-byte-budget"); packageLimit.Package["budgets"]!["maxPackageBytes"] = 1; packageLimit.SavePackage(); Reject(packageLimit, "actual package size exceeds declared manifest budget");
        foreach (var (name, path, mutate) in new (string, string, Func<byte[], byte[]>)[]
        {
            ("png-wrong-signature", Fixture.TexturePath, bytes => { bytes[0] = 0; return bytes; }),
            ("png-truncated-envelope", Fixture.TexturePath, bytes => bytes[..^2]),
            ("png-invalid-ihdr-length", Fixture.TexturePath, bytes => { bytes[11] = 12; return bytes; }),
            ("png-overflow-chunk", Fixture.TexturePath, bytes => { bytes[8] = 127; bytes[9] = 255; bytes[10] = 255; bytes[11] = 255; return bytes; }),
            ("glb-wrong-signature", Fixture.MeshPath, bytes => { bytes[0] = 0; return bytes; }),
            ("glb-truncated-envelope", Fixture.MeshPath, bytes => bytes[..^1]),
            ("glb-invalid-version", Fixture.MeshPath, bytes => { bytes[4] = 1; return bytes; }),
            ("glb-chunk-outside-envelope", Fixture.MeshPath, bytes => { bytes[12] = 255; bytes[13] = 255; bytes[14] = 255; bytes[15] = 127; return bytes; }),
            ("glb-external-uri", Fixture.MeshPath, _ => Fixture.Glb("untracked.bin")),
            ("glb-data-uri", Fixture.MeshPath, _ => Fixture.Glb("data:application/octet-stream;base64,AAAAAA=="))
        })
        {
            var bad = Make(name); bad.Write(path, mutate(File.ReadAllBytes(bad.Full(path))));
            var index = path == Fixture.TexturePath ? 0 : 1;
            bad.Package["resources"]![index]!["sha256"] = bad.Hash(path);
            bad.Package["resources"]![index]!["bytes"] = new FileInfo(bad.Full(path)).Length;
            bad.SavePackage(); Reject(bad, "current hash still rejects malformed/unclosed " + name);
        }

        Check.That(VfxInspection.Inspect(good.Workspace(), good.Full(Fixture.PackagePath)).PackagePath == Fixture.PackagePath, "safe absolute package argument preserves canonical project-relative identity");
        foreach (var path in new[] { "inputs/effect/../effect/package.json", "inputs/effect/package.json.", "inputs/effect/package.json ", "inputs/effect//package.json" })
            Check.Throws<InvalidOperationException>(() => VfxInspection.Inspect(good.Workspace(), path), "noncanonical package identity rejects: " + path);
        var packageAlias = Make("package-alias");
        packageAlias.Package["resources"]![0]!["path"] = "package.json";
        packageAlias.Package["resources"]![0]!["kind"] = "texture-png"; packageAlias.SavePackage();
        Check.That(!VfxInspection.Inspect(packageAlias.Workspace(), Fixture.PackagePath).IsValid, "package file cannot become a declared texture dependency by an alias");
        var escape = Make("relative-dependency-escape"); escape.Package["resources"]![0]!["path"] = "../other.png"; escape.SavePackage();
        Check.That(!VfxInspection.Inspect(escape.Workspace(), Fixture.PackagePath).IsValid, "dependency traversal is rejected semantically before dependency reads");
        var collision = Make("case-collision"); collision.Package["resources"]![1]!["path"] = "TEXTURES/SPARK.PNG"; collision.SavePackage();
        Check.That(!VfxInspection.Inspect(collision.Workspace(), Fixture.PackagePath).IsValid, "portable case-colliding dependency paths reject");
        var caseAlias = Make("filesystem-case-alias"); caseAlias.Package["resources"]![0]!["path"] = "textures/Spark.png"; caseAlias.SavePackage();
        Reject(caseAlias, "filesystem case alias does not bind exact manifest path");
        var transplanted = Make("transplanted-resource");
        transplanted.Write(Fixture.TexturePath, Encoding.UTF8.GetBytes("different original fixture package texture"));
        Reject(transplanted, "resource transplanted from a different package cannot reuse declared digest");

        var invalid = Make("invalid-does-not-read-resources"); invalid.Package["effects"]![0]!["layers"]![0]!["kind"] = "arbitrary-shader"; invalid.SavePackage();
        File.Delete(invalid.Full(Fixture.TexturePath));
        var invalidResult = VfxInspection.Inspect(new ReadOnlyWorkspace(invalid.Workspace()), Fixture.PackagePath);
        Check.That(!invalidResult.IsValid && invalidResult.ObservedResources.Count == 0, "semantic invalid input returns issues without reading absent resource files");
        var malformed = Make("malformed"); malformed.Write(Fixture.PackagePath, Encoding.UTF8.GetBytes("{\"execute\":\"untrusted.py\"}")); Reject(malformed, "malformed recipe rejects without execution");
        foreach (var operation in new[] { "render", "process", "prepare", "decode-video", "generate", "record", "promote", "Inspect", "" })
            Check.Throws<InvalidOperationException>(() => VfxCommands.Execute(good.Workspace(), operation, Fixture.PackagePath), "unavailable operation rejects: " + operation);
        Check.Throws<InvalidOperationException>(() => VfxCommands.Execute(good.Workspace(), "inspect", Fixture.PackagePath), "public operation requires a validated host context");

        var unregistered = Make("unregistered-package"); unregistered.Context["references"]!.AsArray().RemoveAt(0); unregistered.SaveContext();
        RejectHost(unregistered, "readable input-root package still requires an exact registered input reference");
        var wrongKind = Make("reference-kind"); wrongKind.Context["references"]![0]!["kind"] = "review"; wrongKind.SaveContext();
        RejectHost(wrongKind, "review reference cannot substitute for input reference");
        var style = Make("style-transplant"); style.Package["styleSha256"] = new string('0', 64); style.SavePackage(); style.RefreshPackageReference();
        RejectHost(style, "package cannot transplant a different STYLE identity into current host");
        foreach (var field in new[] { "id", "contractSha256", "definitionSha256", "registrySha256" })
        {
            var engine = Make("engine-transplant-" + field);
            engine.Package["engine"]![field] = field == "id" ? "UNITY" : new string('0', 64);
            engine.SavePackage(); engine.RefreshPackageReference();
            RejectHost(engine, "package cannot transplant host engine " + field);
        }
        foreach (var field in new[] { "contractSha256", "definitionSha256", "registrySha256" })
        {
            var missingEngine = Make("missing-host-engine-" + field);
            var value = missingEngine.Context["policy"]!["engine"]!.AsObject(); value.Remove(field);
            if (field == "contractSha256") value["selectionSource"] = "default";
            missingEngine.SaveContext();
            RejectHost(missingEngine, "public inspection requires exact host engine field " + field);
        }
        var staleStyle = Make("actual-style-drift"); var staleStyleWorkspace = staleStyle.HostWorkspace();
        File.AppendAllText(staleStyle.Full("STYLE.md"), "changed\n");
        Check.Throws<InvalidOperationException>(() => VfxCommands.Execute(staleStyleWorkspace, "inspect", Fixture.PackagePath), "actual registered STYLE bytes drift invalidates loaded host context");
        var staleEngine = Make("actual-engine-drift"); var staleEngineWorkspace = staleEngine.HostWorkspace();
        File.AppendAllText(staleEngine.Full("inputs/engine-definition.json"), " ");
        Check.Throws<InvalidOperationException>(() => VfxCommands.Execute(staleEngineWorkspace, "inspect", Fixture.PackagePath), "actual engine definition bytes drift invalidates loaded host context");
        var staleInput = Make("actual-input-drift"); var staleInputWorkspace = staleInput.HostWorkspace();
        File.AppendAllText(staleInput.Full(Fixture.PackagePath), " ");
        Check.Throws<InvalidOperationException>(() => VfxCommands.Execute(staleInputWorkspace, "inspect", Fixture.PackagePath), "actual registered package byte drift invalidates loaded context");

        var inspectionOnly = Make("inspection-only-host");
        inspectionOnly.Context["policy"] = new JsonObject { ["purpose"] = "read-only-blender-inspection", ["receipt"] = new JsonObject { ["path"] = "STYLE.md", ["sha256"] = inspectionOnly.Hash("STYLE.md") } };
        inspectionOnly.SaveContext(); RejectHost(inspectionOnly, "Blender-only inspection context supplies no VFX art/engine authority");
        var mediaOnly = Make("media-only-host");
        mediaOnly.Context["policy"] = new JsonObject { ["purpose"] = "local-media", ["pipeline"] = "LOCAL", ["media"] = new JsonObject { ["path"] = "STYLE.md", ["sha256"] = mediaOnly.Hash("STYLE.md") } };
        mediaOnly.SaveContext(); RejectHost(mediaOnly, "media-only context supplies no VFX art/engine authority");

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Check.Throws<OperationCanceledException>(() => VfxInspection.Inspect(new ReadOnlyWorkspace(good.Workspace()), Fixture.PackagePath, cancelled.Token), "pre-cancelled inspection stops before file work");
        Check.Throws<OperationCanceledException>(() => VfxCommands.Execute(host, "inspect", Fixture.PackagePath, cancelled.Token), "pre-cancelled public operation stops before admission work");
        using var duringRead = new CancellationTokenSource();
        var cancelWorkspace = new ReadOnlyWorkspace(good.Workspace(), path => { if (path.Replace('\\', '/').EndsWith(Fixture.TexturePath, StringComparison.Ordinal)) duringRead.Cancel(); });
        Check.Throws<OperationCanceledException>(() => VfxInspection.Inspect(cancelWorkspace, Fixture.PackagePath, duringRead.Token), "cancellation after dependency path admission stops inspection");
        Check.That(Equal(before, good.Snapshot()) && cancelWorkspace.WriteCalls == 0, "cancellation preserves every input and creates no output");

        var linked = Make("reparse-dependency");
        var link = linked.Full("inputs/effect/linked"); var target = linked.Full("inputs/effect/textures");
        if (TryDirectoryLink(link, target, out var limitation))
        {
            linked.Package["resources"]![0]!["path"] = "linked/spark.png"; linked.SavePackage();
            Check.Throws<InvalidOperationException>(() => VfxInspection.Inspect(linked.Workspace(), Fixture.PackagePath), "actual directory symlink/reparse dependency is rejected");
        }
        else Check.Limits.Add(limitation);
    }

    static void Reject(Fixture fixture, string name)
    {
        var before = fixture.Snapshot(); var workspace = new ReadOnlyWorkspace(fixture.Workspace());
        Check.Throws<InvalidOperationException>(() => VfxInspection.Inspect(workspace, Fixture.PackagePath), name);
        Check.That(Equal(before, fixture.Snapshot()) && workspace.WriteCalls == 0, name + " performs no writes");
    }
    static void RejectHost(Fixture fixture, string name)
    {
        var before = fixture.Snapshot(); var workspace = new ReadOnlyWorkspace(fixture.HostWorkspace());
        Check.Throws<InvalidOperationException>(() => VfxCommands.Execute(workspace, "inspect", Fixture.PackagePath), name);
        Check.That(Equal(before, fixture.Snapshot()) && workspace.WriteCalls == 0, name + " leaves inputs and state unchanged");
    }
    internal static bool Equal(Dictionary<string, string> left, Dictionary<string, string> right) => left.Count == right.Count && left.All(x => right.TryGetValue(x.Key, out var hash) && hash == x.Value);

    static bool TryDirectoryLink(string link, string target, out string limitation)
    {
        limitation = "";
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (OperatingSystem.IsWindows() && !link.Concat(target).Any(x => "&|<>^%!\r\n\"".Contains(x)))
            {
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var value in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(value);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot create owned reparse fixture");
                var output = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
                if (process.ExitCode == 0) return true;
                limitation = "Actual reparse-point fixture unavailable: " + output.Trim() + " " + stderr.Trim(); return false;
            }
            limitation = "Actual reparse-point fixture unavailable: " + error.GetType().Name; return false;
        }
    }
}

internal sealed class ReadOnlyWorkspace(Workspace inner, Action<string>? beforeResolve = null) : IWorkspace
{
    internal int WriteCalls { get; private set; }
    public string Root => inner.Root;
    public HostExecutionContext? HostContext => inner.HostContext;
    public string Resolve(string path) { beforeResolve?.Invoke(path); return inner.Resolve(path); }
    public string Relative(string path) => inner.Relative(path);
    public string HashFile(string path) => inner.HashFile(path);
    public string ResolveWrite(string path) { WriteCalls++; throw new InvalidOperationException("Read-only test workspace prohibits writes"); }
    public void Save<T>(string path, T value) { WriteCalls++; throw new InvalidOperationException("Read-only test workspace prohibits persistence"); }
    public T Load<T>(string path) => inner.Load<T>(path);
    public FileStream Lock(string path) { WriteCalls++; throw new InvalidOperationException("Read-only test workspace prohibits writable lock acquisition"); }
}
