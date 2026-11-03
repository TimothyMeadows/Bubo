using System.Text.Json;
using Bubo.Host;
using Bubo.FloorPlan.Infrastructure;
using Hephaera.AssetProduction;
namespace Bubo.FloorPlan.Tests;

/// <summary>Explicit opt-in development QA only. Reuses native admission/owned jobs, never promotes or grants human acceptance.</summary>
public static class ConstructionBlenderReviewDriver
{
    public static async Task<int> Run(string root, string contextPath, string runtimeDirectory, string jobPath, string blender)
    {
        var repository = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repository, "Bubo.slnx"))) repository = Directory.GetParent(repository)?.FullName ?? throw new InvalidOperationException("QA repository missing");
        var runtime = new Workspace(repository); runtimeDirectory = runtime.Resolve(runtimeDirectory);
        var host = Bubo.Host.HostExecutionContext.Load(root, contextPath); host.RequireReference(jobPath, "input"); host.EnsureToolAllowed(blender);
        if (host.Pipeline != "FLOOR_PLAN_TO_MODEL") throw new InvalidOperationException("QA context must explicitly select FLOOR_PLAN_TO_MODEL");
        var workspace = new Workspace(root, host); var job = workspace.Resolve(jobPath);
        if (new FileInfo(job).Length > 1_048_576) throw new InvalidOperationException("Source-review job exceeds 1MiB");
        using var document = JsonDocument.Parse(StrictPlanJson.ReadBytes(job));
        var value = document.RootElement; var source = value.GetProperty("source").GetString()!;
        host.RequireReference(source, "input"); host.EnsureWriteAllowed(value.GetProperty("outputDirectory").GetString()!);
        var receipt = StrictPlanJson.Read<System.Text.Json.Nodes.JsonObject>(Path.Combine(runtimeDirectory, "build-info.json"));
        var build = receipt["capabilities"]!["build"]!; var catalogPath = Path.Combine(runtimeDirectory, "resources/catalog.json");
        if (runtime.HashFile(catalogPath) != build["resourceCatalogSha256"]!.GetValue<string>()) throw new InvalidOperationException("QA runtime catalog changed");
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(catalogPath));
        foreach (var file in new[] { "review.py", "admission.py" })
        {
            var relative = "resources/floor-plan-to-model/blender/" + file; var digest = runtime.HashFile(Path.Combine(runtimeDirectory, relative));
            if (digest != catalog.RootElement.GetProperty("files").GetProperty(relative).GetString() || digest != value.GetProperty("bundle").GetProperty(file).GetString()) throw new InvalidOperationException("QA script is not the exact reviewed resource");
        }
        foreach (var collection in new[] { "assemblies", "launch" })
            foreach (var entry in build[collection]!.AsObject())
                if (runtime.HashFile(Path.Combine(runtimeDirectory, entry.Key)) != entry.Value!.GetValue<string>()) throw new InvalidOperationException("QA runtime launch/assembly identity changed");
        var native = new NativeProcess();
        if (!native.SupportsPrivateMemoryLimit || host.MaxPrivateBytes is null) throw new PlatformNotSupportedException("Explicit supported owned-job memory limit is required for real Blender QA");
        var directory = workspace.Relative(Path.GetDirectoryName(job)!); var temp = workspace.ResolveWrite(directory + "/temp"); Directory.CreateDirectory(temp);
        var environment = new Dictionary<string, string> { ["TEMP"] = temp, ["TMP"] = temp, ["TMPDIR"] = temp, ["PYTHONDONTWRITEBYTECODE"] = "1", ["PYTHONNOUSERSITE"] = "1", ["XDG_CACHE_HOME"] = Path.Combine(temp, "cache"), ["BLENDER_USER_CONFIG"] = Path.Combine(temp, "config"), ["BLENDER_USER_SCRIPTS"] = Path.Combine(temp, "scripts"), ["BLENDER_USER_DATAFILES"] = Path.Combine(temp, "data"), ["BLENDER_USER_EXTENSIONS"] = Path.Combine(temp, "extensions") };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(host.TimeoutSeconds));
        var process = new HostNativeProcess(host, native); var jobSha = workspace.HashProjectFile(jobPath);
        await process.RunWithLimits(blender, ["--background", "--factory-startup", "--offline-mode", "--disable-autoexec", "--python-exit-code", "1", "--python", Path.Combine(runtimeDirectory, "resources/floor-plan-to-model/blender/review.py"), "--", job, jobSha], workspace.Root, workspace.ResolveWrite(directory + "/review.log"), host.TimeoutSeconds, host.MaxPrivateBytes.Value, cancellation.Token, host.MaxLogBytes, environment);
        host.ValidateFresh(); cancellation.Token.ThrowIfCancellationRequested();
        Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", scope = "Actual Blender source inspection/CPU renders only; no model export, human approval or runtime acceptance", sourceReview = value.GetProperty("outputDirectory").GetString() }, StrictPlanJson.Options));
        return 0;
    }
}
