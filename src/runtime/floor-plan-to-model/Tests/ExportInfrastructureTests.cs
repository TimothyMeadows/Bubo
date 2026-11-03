using System.Text.Json;
using System.Security.Cryptography;
using Hephaera.AssetProduction;
using Bubo.FloorPlan.Infrastructure;

namespace Bubo.FloorPlan.Tests;

public static class ExportInfrastructureTests
{
    sealed record Payload(string Data);

    public static void Run()
    {
        var repository = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repository, "Bubo.slnx"))) repository = Directory.GetParent(repository)?.FullName ?? throw new InvalidOperationException("Fixture repository missing");
        var root = Path.Combine(repository, ".bubo", "export-json-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string Write(string name, string value) { var path = Path.Combine(root, name); File.WriteAllText(path, value); return path; }
        Check.Case("export durable state: oversized replacement preserves readable original bytes and creates no temporary file", () =>
        {
            var folder = Path.Combine(root, "bounded-state"); Directory.CreateDirectory(folder); var workspace = new Workspace(folder);
            ExportJson.SaveBounded(workspace, "exports.json", new Payload("retained readable history"), StrictPlanJson.MaximumInputBytes);
            var before = File.ReadAllBytes(Path.Combine(folder, "exports.json"));
            Check.Throws<InvalidOperationException>(() => ExportJson.SaveBounded(workspace, "exports.json", new Payload(new string('a', StrictPlanJson.MaximumInputBytes)), StrictPlanJson.MaximumInputBytes));
            Check.True(File.ReadAllBytes(Path.Combine(folder, "exports.json")).SequenceEqual(before));
            Check.True(StrictPlanJson.Read<Payload>(Path.Combine(folder, "exports.json")).Data == "retained readable history");
            Check.True(Directory.GetFiles(folder).Length == 1);
        });
        Check.Case("export durable state: exact UTF8 writer bytes admit boundary and escaped terminal reserve", () =>
        {
            var value = new Payload("Unicode é and escaped control \u0001");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Workspace.Json);
            ExportJson.RequireSavedSize(value, bytes.Length);
            Check.Throws<InvalidOperationException>(() => ExportJson.RequireSavedSize(value, bytes.Length - 1));
            var initial = new { status = "running", error = (string?)null };
            ExportJson.RequireSavedSize(initial, 65_536);
            var terminal = new { status = "failed", error = new string('\u0001', 4096) };
            ExportJson.RequireSavedSize(terminal, 65_536);
        });
        Check.Case("export reference: large bounded construction JSON does not relax the design-input ceiling", () =>
        {
            var data = new string('a', StrictPlanJson.MaximumInputBytes + 123);
            var file = Write("large.json", JsonSerializer.Serialize(new Payload(data), StrictPlanJson.Options));
            var parsed = ExportJson.Read<Payload>(file, 16 * 1024 * 1024, default);
            Check.True(parsed.Data == data);
            Check.Throws<InvalidOperationException>(() => StrictPlanJson.Read<Payload>(file));
            Check.Throws<InvalidOperationException>(() => ExportJson.Read<Payload>(file, StrictPlanJson.MaximumInputBytes, default));
        });
        Check.Case("export reference: nested duplicate keys are rejected before object materialization", () =>
        {
            var file = Write("duplicate.json", "{\"outer\":[{\"inner\":1,\"inner\":2}]}");
            Check.Throws<InvalidOperationException>(() => ExportJson.Read<Dictionary<string, JsonElement>>(file, 4096, default));
        });
        Check.Case("export reference: overflowing numeric values cannot hide in unknown JSON data", () =>
        {
            var file = Write("nonfinite.json", "{\"outer\":1e999}");
            Check.Throws<InvalidOperationException>(() => ExportJson.Read<Dictionary<string, JsonElement>>(file, 4096, default));
        });
        Check.Case("export reference: closed DTO still rejects unknown, alternate-case and null required fields", () =>
        {
            foreach (var (name, json) in new[] { ("unknown", "{\"data\":\"ok\",\"extra\":1}"), ("case", "{\"Data\":\"ok\"}"), ("null", "{\"data\":null}") })
                Check.Throws<JsonException>(() => ExportJson.Read<Payload>(Write(name + ".json", json), 4096, default));
        });
        Check.Case("export reference: finite nesting bound survives the larger byte allowance", () =>
        {
            var file = Write("depth.json", new string('[', 65) + "0" + new string(']', 65));
            Check.Throws<JsonException>(() => ExportJson.Read<JsonElement>(file, 4096, default));
        });
        Check.Case("export reference: cancellation and invalid ceilings stop before reading", () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Check.Throws<OperationCanceledException>(() => ExportJson.Read<Payload>(Path.Combine(root, "not-created.json"), 4096, cancellation.Token));
            Check.Throws<InvalidOperationException>(() => ExportJson.Read<Payload>(Path.Combine(root, "not-created.json"), ExportJson.HardMaximumBytes + 1, default));
        });
        (string Tool, string Module, string Sha) Toolchain(string name)
        {
            var installation = Path.Combine(root, name); Directory.CreateDirectory(installation);
            var tool = Path.Combine(installation, "blender-fixture.txt"); File.WriteAllText(tool, "Never executed; synthetic bundled-module identity fixture.");
            var module = Path.Combine(installation, "4.5", "scripts", "addons_core", "io_scene_gltf2"); Directory.CreateDirectory(module);
            File.WriteAllText(Path.Combine(module, "__init__.py"), "# Synthetic module, never imported.\n");
            return (tool, module, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(tool))));
        }
        Check.Case("export toolchain: stable bundled code and existing bytecode are pinned without execution", () =>
        {
            var fixture = Toolchain("stable"); Directory.CreateDirectory(Path.Combine(fixture.Module, "__pycache__"));
            File.WriteAllText(Path.Combine(fixture.Module, "__pycache__", "fixture.pyc"), "Synthetic bytecode identity; never loaded.");
            var captured = GltfToolchainIdentity.Capture(fixture.Tool, fixture.Sha, default);
            Check.True(captured.Files.Count == 2 && captured.Files.Any(f => f.Path.EndsWith(".pyc", StringComparison.Ordinal)));
            GltfToolchainIdentity.RequireCurrent(captured, default);
            File.AppendAllText(Path.Combine(fixture.Module, "__init__.py"), "# changed\n");
            Check.Throws<InvalidOperationException>(() => GltfToolchainIdentity.RequireCurrent(captured, default));
        });
        Check.Case("export toolchain: an added module dependency invalidates the closed inventory", () =>
        {
            var fixture = Toolchain("additional"); var captured = GltfToolchainIdentity.Capture(fixture.Tool, fixture.Sha, default);
            File.WriteAllText(Path.Combine(fixture.Module, "extra.py"), "# unpinned addition\n");
            Check.Throws<InvalidOperationException>(() => GltfToolchainIdentity.RequireCurrent(captured, default));
        });
        Check.Case("export toolchain: changed executable and oversized module stop before use", () =>
        {
            var fixture = Toolchain("bounds");
            Check.Throws<InvalidOperationException>(() => GltfToolchainIdentity.Capture(fixture.Tool, new string('0', 64), default));
            using (var large = File.Create(Path.Combine(fixture.Module, "too-large.py"))) large.SetLength(16_777_217);
            Check.Throws<InvalidOperationException>(() => GltfToolchainIdentity.Capture(fixture.Tool, fixture.Sha, default));
        });
        if (OperatingSystem.IsWindows())
        {
            Check.Case("actual NTFS export boundary: named file stream cannot hide behind default hash/size", () =>
            {
                var fixture = Path.Combine(root, "file-stream"); Directory.CreateDirectory(Path.Combine(fixture, "package"));
                var file = Path.Combine(fixture, "package/package.json"); File.WriteAllText(file, "{}");
                var workspace = new Workspace(fixture); var payload = new ExportPayload(workspace, new string('f', 64));
                var originalHash = Workspace.Hash(file); var originalLength = new FileInfo(file).Length;
                Check.True(payload.Inventory("package", 4096, default).Count == 1);
                File.WriteAllText(file + ":unlisted-test-data", "Synthetic named stream; project-local fixture only.");
                Check.True(Workspace.Hash(file) == originalHash && new FileInfo(file).Length == originalLength);
                Check.Throws<InvalidOperationException>(() => payload.Inventory("package", 4096, default));
                var budget = new ExportInvocationBudget(workspace, "package", ConstructionPlanExamples.SingleStorey(), 1024);
                Check.Throws<InvalidOperationException>(() => budget.RemainingBytes(default));
            });
            Check.Case("actual NTFS export boundary: directory streams are included in closed-byte admission", () =>
            {
                foreach (var relative in new[] { "package", "package/drawings" })
                {
                    var fixture = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(fixture, relative));
                    var directory = Path.Combine(fixture, relative); File.WriteAllText(directory + ":unlisted-directory-data", "Synthetic directory stream.");
                    var workspace = new Workspace(fixture); var payload = new ExportPayload(workspace, new string('f', 64));
                    Check.Throws<InvalidOperationException>(() => payload.Inventory("package", 4096, default));
                    Check.Throws<InvalidOperationException>(() => new ExportInvocationBudget(workspace, "package", ConstructionPlanExamples.SingleStorey(), 1024).RemainingBytes(default));
                }
            });
        }
    }
}
