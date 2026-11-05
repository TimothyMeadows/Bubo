using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Host;
using HostExecutionContext = Bubo.Host.HostExecutionContext;
using Hephaera.AssetProduction;
using ProofApplication = Hephaera.ProofToModel.Application;
using ProofInfrastructure = Hephaera.ProofToModel.Infrastructure;
using MotionApplication = Hephaera.ModelAnimation.Application;
using MotionInfrastructure = Hephaera.ModelAnimation.Infrastructure;
using ActionApplication = Hephaera.ModelAction.Application;
using ActionInfrastructure = Hephaera.ModelAction.Infrastructure;

var resultOutput = Console.Out;
var workflow = args.Length > 0 ? args[0] : "help";
var operation = args.Length > 1 ? args[1] : "help";
if(workflow=="gpu-guardian" && operation=="run")
{
    RuntimeCatalog.Verify();
    return await GpuGuardian.Serve(args[2..]);
}
try
{
    if (args.Length == 0 || args is ["--help"] or ["help"])
    {
        resultOutput.WriteLine("bubo <proof-to-model|model-to-animation|model-to-action|blender-inspection|media|floor-plan-to-model|schematic-to-model|vfx-assets> <operation> --api-version 2 --root ROOT --host-context PROJECT_JSON [--manifest PROJECT_JSON] [operation options]\nbubo capabilities --api-version 2\nvfx-assets: validate|inspect --package JSON; prepare --request JSON --output STAGING_DIRECTORY; verify-preparation --manifest PREPARATION_JSON. Fixed lossless PNG sheet/sequence/math masks only; no video, rendering, generation or acceptance. media: inspect [--load true], generate, setup [--execute]. Schematic: init --design JSON, doctor, preview, validate, record, build-parts, assemble, prove-motion, export --author NAME, review, status. Native steps use --blender ABSOLUTE_EXE. Assembly and export require approved explicit v2 design and current independent parts/readiness. Model-to-animation mechanical-assembly init additionally requires --handoff JSON. Explicit build/setup precedes production. Host context supplies policy, permitted roots, rights/consent, tool hashes, budgets and reviews. Results are JSON on stdout; logs use stderr. No host instruction loading, implicit installation, provider fallback or automatic asset acceptance.");
        return 0;
    }
    var capability = workflow == "capabilities";
    var options = ParseOptions(args, capability ? 1 : 2);
    if (options.GetValueOrDefault("api-version") != "2")
        throw new InvalidOperationException("Explicit --api-version 2 is required");
    RuntimeCatalog.Verify();
    var capabilities = Capabilities();
    if (capability)
    {
        if (options.Keys.Any(key => key != "api-version")) throw new InvalidOperationException("capabilities accepts only --api-version");
        resultOutput.WriteLine(JsonSerializer.Serialize(capabilities, Workspace.Json));
        return 0;
    }
    ValidateOperation(workflow, operation, options);
    var root = options.GetValueOrDefault("root") ?? throw new InvalidOperationException("Explicit --root required");
    var context = HostExecutionContext.Load(root, options.GetValueOrDefault("host-context") ?? throw new InvalidOperationException("Explicit --host-context required"));
    if (context.InspectionOnly && workflow != "blender-inspection")
        throw new InvalidOperationException("Inspection-only context cannot execute an asset workflow");
    if (context.MediaOnly && workflow != "media")
        throw new InvalidOperationException("Local-media context cannot execute an asset workflow or inspection");
    if (options.TryGetValue("task", out var requestedTask) && requestedTask != context.TaskId)
        throw new InvalidOperationException("Task conflicts with host context");
    if (workflow == "vfx-assets")
    {
        // Package inspection is read-only: it neither initializes lifecycle state
        // nor enters a native authoring/processing or asset acceptance path.
        using var inspectionCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(context.TimeoutSeconds));
        ConsoleCancelEventHandler cancelInspection = (_, eventArgs) => { eventArgs.Cancel = true; inspectionCancellation.Cancel(); };
        Console.CancelKeyPress += cancelInspection;
        try
        {
            if (operation is "prepare" or "verify-preparation")
            {
                var nativeBuild = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(capabilities["build"]!.ToJsonString())));
                var vfxWorkspace = new Workspace(context.Root, context);
                var prepared = operation == "prepare"
                    ? Bubo.Vfx.Infrastructure.VfxPreparation.Prepare(vfxWorkspace,
                        options.GetValueOrDefault("request") ?? throw new InvalidOperationException("Explicit --request required"),
                        options.GetValueOrDefault("output") ?? throw new InvalidOperationException("Explicit --output required"), nativeBuild, inspectionCancellation.Token)
                    : Bubo.Vfx.Infrastructure.VfxPreparation.Verify(vfxWorkspace,
                        options.GetValueOrDefault("manifest") ?? throw new InvalidOperationException("Explicit --manifest required"), nativeBuild, inspectionCancellation.Token);
                resultOutput.WriteLine(JsonSerializer.Serialize(new
                {
                    schemaVersion = "bubo-execution-result/v1", apiVersion = 2, status = "succeeded", workflow, operation,
                    hostContextSha256 = context.ContextSha256, data = prepared
                }, Workspace.Json));
                return 0;
            }
            var inspection = Bubo.Vfx.Infrastructure.VfxCommands.Execute(
                new Workspace(context.Root, context), operation,
                options.GetValueOrDefault("package") ?? throw new InvalidOperationException("Explicit --package required"),
                inspectionCancellation.Token);
            resultOutput.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = "bubo-execution-result/v1", apiVersion = 2,
                status = inspection.IsValid ? "succeeded" : "failed", workflow, operation,
                hostContextSha256 = context.ContextSha256, data = inspection
            }, Workspace.Json));
            return inspection.IsValid ? 0 : 1;
        }
        finally { Console.CancelKeyPress -= cancelInspection; }
    }
    if (operation == "promote" && !context.AllowPromotion)
        throw new InvalidOperationException("Host context does not permit promotion; separate current acceptance is also required");
    if (operation == "setup" && options.ContainsKey("execute") && !context.AllowProvisioning)
        throw new InvalidOperationException("Host context does not permit explicit provisioning; no dependency installed");
    if (workflow == "media")
    {
        if (!context.MediaOnly) throw new InvalidOperationException("Media execution requires a local-media purpose context");
        var mediaResult=await MediaCommands.Execute(context,operation,options,capabilities["build"]!.AsObject());
        resultOutput.WriteLine(JsonSerializer.Serialize(mediaResult,Workspace.Json));
        return 0;
    }
    if (workflow == "floor-plan-to-model" && context.Pipeline != "FLOOR_PLAN_TO_MODEL")
        throw new InvalidOperationException("Host context must explicitly select FLOOR_PLAN_TO_MODEL");
    if (workflow == "schematic-to-model" && context.Pipeline != "SCHEMATIC_TO_MODEL")
        throw new InvalidOperationException("Host context must explicitly select SCHEMATIC_TO_MODEL");
    if (workflow == "proof-to-model" && context.Pipeline != "PROOF_TO_MODEL")
        throw new InvalidOperationException("Host context must explicitly select PROOF_TO_MODEL");
    if (workflow is "model-to-animation" or "model-to-action")
    {
        var engine=context.Engine;
        foreach (var field in new[] { "definitionSha256", "registrySha256" }.Concat(engine["selectionSource"]?.GetValue<string>() == "default" ? [] : new[] { "contractSha256" }))
            if (engine[field] is not JsonValue value || !value.TryGetValue<string>(out var hash) || !System.Text.RegularExpressions.Regex.IsMatch(hash,"^[a-f0-9]{64}$"))
                throw new InvalidOperationException("Exported 3D package compatibility requires explicit host engine " + field);
    }
    var workspace = new ProofInfrastructure.Workspace(context.Root, context);
    context.EnsureWriteAllowed(context.StateRoot);
    context.EnsureWriteAllowed(context.StagingRoot);
    if (operation == "promote")
        context.EnsureWriteAllowed(options.GetValueOrDefault("destination") ?? throw new InvalidOperationException("Explicit --destination required"));
    var process = new HostNativeProcess(context, new NativeProcess());
    var pipelineOptions = new Dictionary<string,string>(options, StringComparer.Ordinal);
    pipelineOptions.Remove("api-version"); pipelineOptions.Remove("host-context");
    pipelineOptions["root"] = context.Root;
    pipelineOptions["task"] = context.TaskId;
    pipelineOptions["baseline-root"] = AppContext.BaseDirectory;
    pipelineOptions["state-root"] = context.StateRoot;
    pipelineOptions["staging-root"] = context.StagingRoot;
    var runtimeBuildSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(capabilities["build"]!.ToJsonString())));
    pipelineOptions["runtime-build-sha256"] = runtimeBuildSha256;
    if (workflow == "model-to-action") pipelineOptions["host-style-sha256"] = context.StyleSha256;
    if (workflow is "floor-plan-to-model" or "schematic-to-model") pipelineOptions["runtime-resource-catalog-sha256"] = RuntimeCatalog.ExpectedSha256;
    foreach (var name in new[] { "rights", "recipe", "toolchain", "selection", "library-search", "evidence", "proof-set", "classification", "finish-settings", "finish-budget", "revision", "source-review", "sources", "proof", "model", "motion-package", "motion-source", "mapping", "brief", "proof-metadata", "source-metadata", "editable-source", "artifact", "source", "plan", "design", "parent-manifest", "handoff" })
        if (pipelineOptions.TryGetValue(name, out var value)) context.RequireReference(value);
    if (operation == "init")
    {
        var asset = pipelineOptions.GetValueOrDefault("asset") ?? throw new InvalidOperationException("--asset required");
        var version = pipelineOptions.GetValueOrDefault("version") ?? throw new InvalidOperationException("--version required");
        if (!System.Text.RegularExpressions.Regex.IsMatch(asset, "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$") || !System.Text.RegularExpressions.Regex.IsMatch(version, "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$") || asset == ".." || version == "..")
            throw new InvalidOperationException("Invalid asset/version identity");
        var canonicalManifest=$"{context.StateRoot}/{workflow}/{asset}/{version}/manifest.json";
        if ((workflow is "model-to-action" or "floor-plan-to-model" or "schematic-to-model") && pipelineOptions.TryGetValue("manifest", out var selectedManifest) && workspace.Relative(selectedManifest) != canonicalManifest)
            throw new InvalidOperationException((workflow == "model-to-action" ? "Action" : workflow == "schematic-to-model" ? "Schematic" : "Floor-plan") + " init requires its canonical manifest path: " + canonicalManifest);
        pipelineOptions.TryAdd("manifest", canonicalManifest);
        context.EnsureWriteAllowed(pipelineOptions["manifest"]);
    }
    else if (workflow != "blender-inspection" && !(workflow == "model-to-action" && operation == "inspect-effects"))
    {
        var manifest = pipelineOptions.GetValueOrDefault("manifest") ?? throw new InvalidOperationException("--manifest required");
        var state = workspace.Load<JsonObject>(manifest);
        if (state["task"]?.GetValue<string>() != context.TaskId)
            throw new InvalidOperationException("Manifest belongs to another task");
        if (state["hostPolicySha256"]?.GetValue<string>() != HostPolicyIdentity.Fingerprint(context))
            throw new InvalidOperationException("Host selection changed or this manifest predates the Bubo boundary; initialize a new asset version with retained ancestry and fresh verification");
        if (state["runtimeBuildSha256"]?.GetValue<string>() != runtimeBuildSha256)
            throw new InvalidOperationException("Bubo build identity changed or is missing; preserve historical evidence and initialize a new asset version with fresh affected verification");
    }
    var attempt = context.StateRoot + "/bubo-executions/" + Guid.NewGuid().ToString("N");
    workspace.Save(attempt + "/request.json", new
    {
        schemaVersion = "bubo-execution-request/v1", workflow, operation,
        context = new { path = context.ContextPath, sha256 = context.ContextSha256 },
        runtime = capabilities["build"]!.DeepClone(), manifest = pipelineOptions.GetValueOrDefault("manifest"),
        requestedAt = DateTimeOffset.UtcNow
    });
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    if (workflow is "floor-plan-to-model" or "schematic-to-model") cancellation.CancelAfter(TimeSpan.FromSeconds(context.TimeoutSeconds));
    var logs = new OperationLogWriter(Console.Error);
    Console.SetOut(logs);
    try
    {
        switch (workflow)
        {
            case "proof-to-model":
                await new ProofApplication.Pipeline(workspace, pipelineOptions, new ProofProcess(process), new ProofInfrastructure.AlphaProcessor()).Execute(operation, cancellation.Token);
                break;
            case "model-to-animation":
                await new MotionApplication.Pipeline(workspace, pipelineOptions, process,
                    new MotionInfrastructure.CanonicalEngineResolver(workspace, AppContext.BaseDirectory, process, context.TaskId)).Execute(operation, cancellation.Token);
                break;
            case "model-to-action":
                await new ActionApplication.Pipeline(workspace, pipelineOptions, process,
                    new ActionInfrastructure.EngineResolver(workspace, AppContext.BaseDirectory, process, context.TaskId),
                    new ActionInfrastructure.EffectDependencyInspector()).Execute(operation, cancellation.Token);
                break;
            case "floor-plan-to-model":
                await Bubo.FloorPlan.Infrastructure.FloorPlanCommands.Execute(workspace, process, pipelineOptions, operation, cancellation.Token);
                break;
            case "schematic-to-model":
                await Bubo.Schematic.Infrastructure.SchematicCommands.Execute(workspace, process, pipelineOptions, operation, cancellation.Token);
                break;
            case "blender-inspection":
                await InspectBlender(workspace, context, pipelineOptions, process, attempt, cancellation.Token);
                break;
        }
    }
    catch (Exception error)
    {
        workspace.Save(attempt + "/failure.json", new { schemaVersion = "bubo-execution-result/v1", status = "failed", workflow, operation, error = error.Message, contextSha256 = context.ContextSha256, finishedAt = DateTimeOffset.UtcNow });
        throw;
    }
    finally { Console.CancelKeyPress -= cancel; Console.SetOut(resultOutput); }
    var result = new JsonObject
    {
        ["schemaVersion"] = "bubo-execution-result/v1", ["apiVersion"] = 2,
        ["status"] = "succeeded", ["workflow"] = workflow, ["operation"] = operation,
        ["manifest"] = pipelineOptions.GetValueOrDefault("manifest"),
        ["hostContextSha256"] = context.ContextSha256, ["executionReceipt"] = attempt + "/request.json",
        ["data"] = logs.Data?.DeepClone()
    };
    workspace.Save(attempt + "/result.json", result);
    resultOutput.WriteLine(result.ToJsonString(Workspace.Json));
    return 0;
}
catch (Exception error)
{
    Console.SetOut(resultOutput);
    Console.Error.WriteLine(error.Message);
    var failure = new JsonObject { ["schemaVersion"] = "bubo-execution-result/v1", ["apiVersion"] = 2, ["status"] = "failed", ["workflow"] = workflow, ["operation"] = operation, ["error"] = error.Message };
    if (error is Bubo.FloorPlan.Domain.PlanValidationException invalidPlan) failure["validation"] = JsonSerializer.SerializeToNode(invalidPlan.Report, Workspace.Json);
    if (error is Bubo.Schematic.Domain.DesignValidationException invalidDesign) failure["validation"] = JsonSerializer.SerializeToNode(invalidDesign.Report, Workspace.Json);
    resultOutput.WriteLine(failure.ToJsonString(Workspace.Json));
    return 1;
}

static Dictionary<string,string> ParseOptions(string[] arguments, int start)
{
    var result = new Dictionary<string,string>(StringComparer.Ordinal);
    for (var index = start; index < arguments.Length; index++)
    {
        if (!arguments[index].StartsWith("--", StringComparison.Ordinal)) throw new InvalidOperationException("Expected --option value");
        var key = arguments[index][2..];
        var value = key == "execute" && (index + 1 == arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal)) ? "true"
            : index + 1 < arguments.Length && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal) ? arguments[++index]
            : throw new InvalidOperationException("Missing value for --" + key);
        if (key == "execute" && value != "true")
            throw new InvalidOperationException("--execute accepts only the explicit true flag; omit it for dry-run setup");
        if (!result.TryAdd(key,value)) throw new InvalidOperationException("Duplicate option: --" + key);
    }
    return result;
}

static void ValidateOperation(string workflow, string operation, Dictionary<string,string> options)
{
    var common = new HashSet<string>(["api-version", "root", "host-context", "manifest"], StringComparer.Ordinal);
    if (workflow == "vfx-assets") common.Remove("manifest");
    string[] specific = (workflow,operation) switch
    {
        ("vfx-assets", "validate" or "inspect") => ["package"],
        ("vfx-assets", "prepare") => ["request","output"],
        ("vfx-assets", "verify-preparation") => ["manifest"],
        ("proof-to-model", "init") => ["task","asset","version","actor","proof","sources","mode","rights","recipe","toolchain","brief","proof-metadata","selection","library-search","reuse-manifest"],
        ("proof-to-model", "classify") => ["classification"],
        ("proof-to-model", "prepare-proofs") => ["proof-set"],
        ("proof-to-model", "reuse") => ["reuse-manifest"],
        ("proof-to-model", "record") => ["kind","reviewer","evidence","artifact","source"],
        ("proof-to-model", "setup") => ["execute"],
        ("proof-to-model", "finish") => ["blender","timeout","gpu","finish-settings","finish-budget"],
        ("proof-to-model", "review") => ["blender","timeout","gpu"],
        ("proof-to-model", "preprocess" or "reconstruct" or "doctor") => ["timeout","gpu","blender"],
        ("proof-to-model", "validate" or "status") => [],
        ("model-to-animation", "init") => ["task","asset","version","actor","model","rights","recipe","blender","editable-source","source-metadata","handoff"],
        ("model-to-animation", "author") => ["revision"],
        ("model-to-animation", "retarget") => ["motion-source","mapping"],
        ("model-to-animation", "record") => ["kind","reviewer","evidence"],
        ("model-to-animation", "doctor" or "inspect" or "prepare" or "rig" or "bake" or "export" or "review" or "validate" or "status") => [],
        ("model-to-action", "init") => ["task","asset","version","actor","motion-package","recipe","source-review","blender"],
        ("model-to-action", "inspect-effects") => ["recipe"],
        ("model-to-action", "record") => ["kind","reviewer","evidence"],
        ("model-to-action", "doctor" or "inspect" or "compose" or "export" or "review" or "validate" or "status") => [],
        ("proof-to-model" or "model-to-animation" or "model-to-action", "promote") => ["destination"],
        ("blender-inspection", "inspect") => ["blender"],
        ("floor-plan-to-model", "init") => ["task","asset","version","plan","parent-manifest"],
        ("floor-plan-to-model", "record") => ["kind","reviewer","evidence"],
        ("floor-plan-to-model", "doctor" or "build" or "review") => ["blender"],
        ("floor-plan-to-model", "export") => ["blender","author"],
        ("floor-plan-to-model", "preview" or "validate" or "status") => [],
        ("schematic-to-model", "init") => ["task","asset","version","design","parent-manifest"],
        ("schematic-to-model", "record") => ["kind","reviewer","evidence"],
        ("schematic-to-model", "doctor" or "build-parts" or "assemble" or "prove-motion") => ["blender"],
        ("schematic-to-model", "export") => ["blender","author"],
        ("schematic-to-model", "review") => ["blender"],
        ("schematic-to-model", "preview" or "validate" or "status") => [],
        ("media", "inspect") => ["load"],
        ("media", "generate") => [],
        ("media", "setup") => ["execute"],
        _ => throw new InvalidOperationException("Unsupported workflow/operation")
    };
    common.UnionWith(specific);
    // The retained v1 workflow requires an explicit diagnostic choice on every
    // operation. Preserve that guard through the public versioned boundary.
    if (workflow == "proof-to-model") common.Add("mode");
    foreach (var key in options.Keys)
        if (!common.Contains(key)) throw new InvalidOperationException("Unknown or inapplicable option: --" + key);
}

static JsonObject Capabilities()
{
    var assemblies = new JsonObject();
    foreach (var path in Directory.GetFiles(AppContext.BaseDirectory, "*.dll").Order(StringComparer.Ordinal)) assemblies[Path.GetFileName(path)] = Workspace.Hash(path);
    var launch = new JsonObject();
    foreach (var name in new[] { "bubo.deps.json", "bubo.runtimeconfig.json", OperatingSystem.IsWindows() ? "bubo.exe" : "bubo" })
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(path)) launch[name] = Workspace.Hash(path);
        else if (name.EndsWith(".json", StringComparison.Ordinal)) throw new InvalidOperationException("Missing Bubo launch configuration: " + name);
    }
    return new JsonObject
    {
        ["schemaVersion"] = "bubo-capabilities/v1", ["apiVersions"] = new JsonArray(2),
        ["workflows"] = new JsonArray("proof-to-model","model-to-animation","model-to-action","blender-inspection","media","floor-plan-to-model","schematic-to-model","vfx-assets"),
        ["actions"] = new JsonObject { ["recipeVersions"] = new JsonArray(1,2), ["packageSchemas"] = new JsonArray("hephaera-action-package/v1","hephaera-action-package/v2"), ["effectSubjectInspection"] = true, ["effectSources"] = "complete-native-package-profile-closure", ["effectRuntimeReview"] = "selected-engine-evidence-required", ["mechanicalAssemblyActionsAvailable"] = false },
        ["vfx"] = new JsonObject { ["phase"] = "fixed-input-preparation", ["operations"] = new JsonArray("validate","inspect","prepare","verify-preparation"), ["packageSchema"] = "bubo-vfx-package/v1", ["packageSchemas"] = new JsonArray("bubo-vfx-package/v1","bubo-vfx-package/v2"), ["preparationSchema"] = "bubo-vfx-preparation/v1", ["processingProfiles"] = new JsonArray("sheet","sequence","math-mask"), ["processingAvailable"] = true, ["videoDecodingAvailable"] = false, ["hardProcessingMemoryAvailable"] = false, ["renderingAvailable"] = false, ["runtimeAcceptanceAvailable"] = false },
        ["schematic"] = new JsonObject { ["phase"] = "portable-assembly-export", ["operations"] = new JsonArray("init","doctor","preview","validate","record","build-parts","assemble","prove-motion","export","review","status"), ["partConstructionAvailable"] = true, ["assemblyAvailable"] = true, ["motionProofAvailable"] = true, ["requiresApprovedAssemblyV2"] = true, ["modelExportAvailable"] = true, ["motionHandoffProfile"] = "mechanical-assembly", ["humanModelAcceptanceSeparate"] = true },
        ["floorPlan"] = new JsonObject { ["phase"] = "architectural-export", ["operations"] = new JsonArray("init","doctor","preview","validate","record","build","export","review","status"), ["modelingAvailable"] = true, ["requiresApprovedConstructionPlanV2"] = true, ["modelExportAvailable"] = true },
        ["modelRuntime"] = "pinned-native-tools", ["mediaBackends"] = JsonSerializer.SerializeToNode(Bubo.Media.Domain.MediaCapabilities.Implemented,Bubo.Media.Domain.MediaJson.Options),
        ["build"] = new JsonObject { ["cliSha256"] = Workspace.Hash(Assembly.GetExecutingAssembly().Location), ["resourceCatalogSha256"] = RuntimeCatalog.ExpectedSha256, ["assemblies"] = assemblies, ["launch"] = launch },
        ["platform"] = new JsonObject { ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription, ["nativeProcessOwnershipSupported"] = OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), ["osSandbox"] = false }
    };
}

static async Task InspectBlender(IWorkspace workspace, HostExecutionContext context, Dictionary<string,string> options,
    INativeProcess process, string attempt, CancellationToken cancellation)
{
    var manifest = options.GetValueOrDefault("manifest") ?? throw new InvalidOperationException("Inspection requires a registered --manifest job");
    context.RequireReference(manifest);
    var job = workspace.Load<JsonObject>(manifest);
    if (job["schemaVersion"]?.GetValue<int>() != 1) throw new InvalidOperationException("Inspection job requires schemaVersion 1");
    var scene = job["scene"]?.GetValue<string>() ?? throw new InvalidOperationException("Inspection scene required");
    context.RequireReference(scene);
    if (Path.GetExtension(scene) != ".blend") throw new InvalidOperationException("Inspection requires a local .blend scene");
    var output = job["output"]?.GetValue<string>() ?? throw new InvalidOperationException("Inspection output required");
    context.EnsureWriteAllowed(output);
    if (File.Exists(workspace.Resolve(output))) throw new InvalidOperationException("Inspection output already exists");
    Directory.CreateDirectory(Path.GetDirectoryName(workspace.Resolve(output))!);
    var profile = job["profile"]?.GetValue<string>() ?? "static-asset";
    if (profile is not ("static-asset" or "rigged-actor" or "procedural-scene" or "render-scene" or "simulation")) throw new InvalidOperationException("Unsupported inspection profile");
    var script = Path.Combine(AppContext.BaseDirectory,"resources/blender/inspect-blender-scene.py");
    await process.Run(options.GetValueOrDefault("blender") ?? throw new InvalidOperationException("Explicit --blender required"),
        ["--background","--factory-startup","--disable-autoexec","--offline-mode",workspace.Resolve(scene),"--python-exit-code","1","--python",script,"--","--root",workspace.Root,"--scene-relative",workspace.Relative(scene),"--profile",profile,"--output",workspace.Resolve(output)],
        workspace.Root,workspace.Resolve(attempt+"/inspection.log"), Math.Min(180,context.TimeoutSeconds), cancellation, context.MaxLogBytes);
    if (!File.Exists(workspace.Resolve(output))) throw new InvalidOperationException("Blender inspection produced no result");
    Console.WriteLine(File.ReadAllText(workspace.Resolve(output)));
}

sealed class OperationLogWriter(TextWriter logs) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;
    public JsonNode? Data { get; private set; }
    public override void Write(char value) => logs.Write(value);
    public override void Write(string? value) => logs.Write(value);
    public override void WriteLine(string? value)
    {
        logs.WriteLine(value);
        if (value is null) return;
        if (value.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Machine result exceeds bounded JSON size");
        try { Data = JsonNode.Parse(value); }
        catch (JsonException) { if (Data is null) Data = JsonValue.Create(value); }
    }
}

sealed class ProofProcess(INativeProcess inner) : ProofApplication.INativeProcess
{
    public bool SupportsPrivateMemoryLimit => inner.SupportsPrivateMemoryLimit;
    public Task Run(string executable, IEnumerable<string> arguments, string cwd, string log, int seconds,
        CancellationToken cancellation = default, long maximumLogBytes = 16777216, IReadOnlyDictionary<string,string>? environment = null)
        => inner.Run(executable,arguments,cwd,log,seconds,cancellation,maximumLogBytes,environment);
    public Task RunWithLimits(string executable, IEnumerable<string> arguments, string cwd, string log, int seconds,
        long maximumPrivateBytes, CancellationToken cancellation = default, long maximumLogBytes = 16777216,
        IReadOnlyDictionary<string,string>? environment = null)
        => inner.RunWithLimits(executable,arguments,cwd,log,seconds,maximumPrivateBytes,cancellation,maximumLogBytes,environment);
    public Task RunGpu(GpuProcessSpec request,CancellationToken cancellation=default,Func<CancellationToken,Task>? beforeStart=null)
        => inner.RunGpu(request,cancellation,beforeStart);
}
