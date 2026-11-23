using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using Hephaera.ProofToModel.Application;
using Hephaera.ProofToModel.Domain;
using Hephaera.ProofToModel.Infrastructure;

/// <summary>CPU metadata/filesystem fixtures only; no matting/model executable.</summary>
static class CanvasRecoveryTests
{
    static byte[] Bytes(JsonObject value) => Encoding.UTF8.GetBytes(value.ToJsonString());
    static JsonObject Reference(string path, bool sized = false) => sized ? new() { ["path"] = path, ["sha256"] = new string('a', 64), ["bytes"] = 32 } : new() { ["path"] = path, ["sha256"] = new string('a', 64) };
    static JsonObject Profile() => new()
    {
        ["schemaVersion"] = 1, ["id"] = CanvasMattingProfile.Id, ["mode"] = "multi-view-four", ["maxMattingChildren"] = 4,
        ["baseToolchain"] = Reference("task/accepted-toolchain.json"), ["executable"] = Reference("tools/canvas/matting.exe", true),
        ["files"] = new JsonArray(Reference("tools/canvas/matting.exe", true), Reference("tools/canvas/ggml.dll", true))
    };
    static JsonObject Lineage(bool prior = true)
    {
        var attempts = new JsonObject(); var directories = new JsonObject(); var history = new JsonObject(); var limits = new JsonObject();
        foreach (var stage in CanvasStageLineage.Stages) { attempts[stage] = new JsonArray(); directories[stage] = new JsonArray(); history[stage] = 0; limits[stage] = stage is "classify" or "prepare-proofs" ? 4 : stage == "preprocess" && prior ? 2 : 1; }
        if (prior) { attempts["preprocess"] = new JsonArray("art/prior/attempts/preprocess-failed"); directories["preprocess"] = attempts["preprocess"]!.DeepClone(); history["preprocess"] = 1; }
        return new() { ["schemaVersion"] = 1, ["kind"] = "proof-to-model-stage-lineage", ["decision"] = "approved", ["task"] = "fixture", ["asset"] = "dawnwarden", ["sourceVersion"] = "phantom-mv4-01", ["targetVersion"] = "phantom-mv4-canvas-01", ["authorization"] = Reference("task/recovery-authority.json"), ["inputApproval"] = Reference("task/exact-input-authority.json"), ["priorManifest"] = prior ? Reference("state/prior/manifest.json") : null, ["priorStateRoot"] = "state/prior", ["priorStaging"] = "art/prior", ["priorAttempts"] = attempts, ["priorDirectories"] = directories, ["priorHistoryCounts"] = history, ["stageLimits"] = limits };
    }
    public static async Task Run(Workspace parent, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        var parsed = CanvasMattingProfile.Parse(Bytes(Profile()));
        check(parsed.Files.Count == 2 && parsed.ExistingRuntimeDependencies.Count == 0, "Canvas profile explicitly binds separate bundle without requiring external CUDA bytes");
        string[] original = ["--models", "absolute-models", "--bg-removal", "birefnet", "--bg-only", "--dump-bg", "absolute-input", "absolute-output"];
        var selected = CanvasMattingProfile.PreserveCanvasArguments(original);
        check(selected.Length == 12 && selected[6] == "--bg-preserve-canvas" && selected[7] == "--require-gpu" && selected[8] == "--gpu" && selected[9] == "0" && !selected.Contains("--views") && original.Length == 8, "Canvas overlay pins required GPU localordinal0, keeps absolute payloads and never edits base template or reconstruction flags");
        foreach (var wrong in new[] { new[] { "--views", "inputs", "--pixal3d-weights", "mv" }, original.Append("--num-views").ToArray(), new[] { "--models", "m", "--bg-removal", "threshold", "--bg-only", "--dump-bg", "i", "o" } })
            await reject(() => Task.Run(() => CanvasMattingProfile.PreserveCanvasArguments(wrong)), "Canvas selection refuses SV/MV/threshold/extra flags rather than leaking into reconstruction");
        foreach (var change in new Action<JsonObject>[] { p => p["mode"] = "multi-view", p => p["maxMattingChildren"] = 5, p => p["files"]!.AsArray().Add(p["files"]![0]!.DeepClone()), p => p["files"]![0]!["bytes"] = 536870912, p => p["files"]![1]!["path"] = "../outside.dll", p => p["extra"] = true })
        { var bad = Profile(); change(bad); await reject(() => Task.Run(() => CanvasMattingProfile.Parse(Bytes(bad))), "Canvas bad mode, count, alias, over-budget or unknown field refuses before native child"); }
        var vendor = Profile(); vendor["existingRuntimeDependencies"] = new JsonArray(new JsonObject { ["absolutePath"] = "C:/Program Files/NVIDIA GPU Computing Toolkit/CUDA/v13.2/bin/x64/cudart64_13.dll", ["sha256"] = new string('b', 64), ["bytes"] = 555120 });
        check(CanvasMattingProfile.Parse(Bytes(vendor)).ExistingRuntimeDependencies.Single().Bytes == 555120, "Existing CUDA prerequisite remains explicit and outside installed-bundle byte accounting");
        foreach (var change in new Action<JsonObject>[] { p => p["existingRuntimeDependencies"]![0]!["absolutePath"] = "C:/arbitrary/provider.dll", p => p["files"]!.AsArray().Add(Reference("tools/canvas/cudart64_13.dll", true)), p => p["existingRuntimeDependencies"]!.AsArray().Add(p["existingRuntimeDependencies"]![0]!.DeepClone()) })
        { var bad = (JsonObject)vendor.DeepClone(); change(bad); await reject(() => Task.Run(() => CanvasMattingProfile.Parse(Bytes(bad))), "Unqualified runtime parent, shadow basename and duplicate DLL refused"); }
        var lineage = CanvasStageLineage.Parse(Bytes(Lineage())); lineage.ValidateScope("fixture", "dawnwarden", "phantom-mv4-canvas-01");
        check(lineage.Limit("preprocess", 1) == 2 && lineage.Limit("reconstruct", 1) == 1 && lineage.Limit("classify", 4) == 4, "One extra preprocessing only; metadata/model budgets remain original");
        check(lineage.PriorAttempts["preprocess"].Length + 1 == lineage.Limit("preprocess", 1), "One new stage exhausts cumulative preprocessing2; another retry cannot fit");
        await reject(() => Task.Run(() => lineage.ValidateScope("fixture", "starseer", "phantom-mv4-canvas-01")), "Dawn recovery receipt cannot authorize a foreign class");
        await reject(() => Task.Run(() => lineage.ValidateScope("fixture", "dawnwarden", "retry-version")), "Lineage cannot reset expenditure by changing target version");
        foreach (var change in new Action<JsonObject>[] { p => p["sourceVersion"] = "phantom-mv4-canvas-01", p => p["stageLimits"]!["reconstruct"] = 2, p => p["stageLimits"]!["finish"] = 2, p => p["priorManifest"] = null, p => p["priorDirectories"]!["preprocess"] = new JsonArray("outside"), p => p["priorHistoryCounts"]!["preprocess"] = 2 })
        { var bad = Lineage(); change(bad); await reject(() => Task.Run(() => CanvasStageLineage.Parse(Bytes(bad))), "Changed lineage scope, expanded limits, erased predecessor or counter mismatch refused"); }
        var initial = CanvasStageLineage.Parse(Bytes(Lineage(false)));
        check(initial.PriorManifest is null && initial.PriorAttempts.Values.All(paths => paths.Length == 0) && initial.Limit("preprocess", 1) == 1, "Explicit zero-prior first-time class gets original one-stage budget");
        var fakeEmpty = Lineage(false); fakeEmpty["stageLimits"]!["preprocess"] = 2;
        await reject(() => Task.Run(() => CanvasStageLineage.Parse(Bytes(fakeEmpty))), "No extra stage may be minted from an empty predecessor");
        foreach (var malformed in new[] { "{}", "null", "[]", Profile().ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"), new string(' ', 65537) })
            await reject(() => Task.Run(() => CanvasMattingProfile.Parse(Encoding.UTF8.GetBytes(malformed))), "Malformed/duplicate/oversized selector metadata refused");
        var root = parent.Resolve("canvas-counter-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var workspace = new Workspace(root);
        var empty = "art/prior/attempts/classify-empty"; var recorded = "art/prior/attempts/classify-recorded";
        Directory.CreateDirectory(workspace.Resolve(empty)); Directory.CreateDirectory(workspace.Resolve(recorded));
        var starts = new[] { new JsonObject { ["event"] = "stage-start", ["stage"] = "classify", ["attempt"] = recorded } };
        var union = CanvasAttemptInventory.Scan(workspace, "art/prior", starts, "classify"); var dirs = CanvasAttemptInventory.Directories(workspace, "art/prior", "classify");
        check(union.Length == 2 && dirs.Length == 2, "Real empty failed start remains spent alongside persisted history");
        var counterReceipt = Lineage(); counterReceipt["priorAttempts"]!["classify"] = new JsonArray(union.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray()); counterReceipt["priorDirectories"]!["classify"] = new JsonArray(dirs.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray()); counterReceipt["priorHistoryCounts"]!["classify"] = 1;
        var counter = CanvasStageLineage.Parse(Bytes(counterReceipt)); counter.ValidatePrior("classify", union, dirs, 1);
        var probe = "art/prior/.canvas-lineage-write-probe"; File.WriteAllText(workspace.Resolve(probe), "SYNTHETIC READONLY PROBE ONLY");
        workspace.Save("fixture-media.json", new { fixtureOnly = true });
        JsonObject HostContext(bool protectDirectory) => new()
        {
            ["schemaVersion"] = "bubo-host-context/v1", ["projectRoot"] = workspace.Root, ["task"] = "canvas-readonly-fixture",
            ["roots"] = new JsonObject { ["state"] = "state", ["staging"] = "art", ["tools"] = new JsonArray("tools"), ["inputs"] = protectDirectory ? new JsonArray("art/prior") : new JsonArray(), ["outputs"] = new JsonArray() },
            ["policy"] = new JsonObject { ["purpose"] = "local-media", ["pipeline"] = "LOCAL", ["media"] = new JsonObject { ["path"] = "fixture-media.json", ["sha256"] = workspace.HashProjectFile("fixture-media.json") } },
            ["references"] = new JsonArray(new JsonObject { ["path"] = "fixture-media.json", ["sha256"] = workspace.HashProjectFile("fixture-media.json"), ["kind"] = "policy" }, new JsonObject { ["path"] = probe, ["sha256"] = workspace.HashProjectFile(probe), ["kind"] = "input" }),
            ["tools"] = new JsonArray(), ["budgets"] = new JsonObject { ["timeoutSeconds"] = 10, ["maxLogBytes"] = 4096 }, ["permissions"] = new JsonObject { ["provisioning"] = false, ["promotion"] = false }
        };
        workspace.Save("probe-only-context.json", HostContext(false));
        var probeOnly = Bubo.Host.HostExecutionContext.Load(workspace.Root, "probe-only-context.json");
        await reject(() => Task.Run(() => probeOnly.EnsureWriteAllowed(probe)), "Probe file is actually readonly in adverse real Host context fixture");
        await reject(() => Task.Run(() => CanvasFileIdentity.RequireReadOnlyRoot(probeOnly, "art/prior")), "A readonly child probe cannot spoof a protected directory while staging remains writable");
        workspace.Save("protected-directory-context.json", HostContext(true));
        var protectedDirectory = Bubo.Host.HostExecutionContext.Load(workspace.Root, "protected-directory-context.json");
        CanvasFileIdentity.RequireReadOnlyRoot(protectedDirectory, "art/prior");
        check(true, "True input-only directory passes direct root write denial check");
        Directory.Delete(workspace.Resolve(recorded));
        check(CanvasAttemptInventory.Scan(workspace, "art/prior", starts, "classify").Length == 2, "History union retains expenditure when an old directory is removed");
        await reject(() => Task.Run(() => counter.ValidatePrior("classify", CanvasAttemptInventory.Scan(workspace, "art/prior", starts, "classify"), CanvasAttemptInventory.Directories(workspace, "art/prior", "classify"), 1)), "Separate directory snapshot detects removal even though history keeps the same spent count");
        Directory.CreateDirectory(workspace.Resolve(recorded)); Directory.CreateDirectory(workspace.Resolve("art/prior/attempts/classify-new"));
        await reject(() => Task.Run(() => counter.ValidatePrior("classify", CanvasAttemptInventory.Scan(workspace, "art/prior", starts, "classify"), CanvasAttemptInventory.Directories(workspace, "art/prior", "classify"), 1)), "New old-root attempt invalidates frozen lineage");
        var defaultManifest = JsonSerializer.Serialize(new Manifest { SchemaVersion = 2 });
        check(!defaultManifest.Contains("PreprocessingProfileFile", StringComparison.Ordinal) && !defaultManifest.Contains("StageLineageFile", StringComparison.Ordinal), "Legacy six-view/default serialization omits unselected recovery fields");
        var selectedManifest = JsonSerializer.Serialize(new Manifest { SchemaVersion = 3, ConditioningProfile = "cardinal-four-v1", PreprocessingProfileFile = "task/profile.json", StageLineageFile = "task/lineage.json" });
        check(selectedManifest.Contains("task/profile.json", StringComparison.Ordinal) && selectedManifest.Contains("task/lineage.json", StringComparison.Ordinal), "Explicit four-view selection retains both immutable selector paths");
        if (OperatingSystem.IsWindows())
        {
            var ordinary = workspace.Resolve("single-link-dependency.bin"); File.WriteAllText(ordinary, "SYNTHETIC NONEXECUTABLE IDENTITY ONLY"); CanvasFileIdentity.NoHardLinks(ordinary);
            var alias = workspace.Resolve("dependency-hardlink.bin");
            if (!CreateHardLinkW(alias, ordinary, IntPtr.Zero)) throw new IOException("Cannot create Windows hardlink regression fixture: " + Marshal.GetLastWin32Error());
            await reject(() => Task.Run(() => CanvasFileIdentity.NoHardLinks(ordinary)), "Registered dependency still refuses an NTFS hardlink despite unchanged ordinary bytes");
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);
}
