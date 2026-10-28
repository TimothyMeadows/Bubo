using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Host;
using HostExecutionContext = Bubo.Host.HostExecutionContext;

var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
var requested = args.Length == 0 ? Path.Combine(project, "bin/test-fixtures")
    : args.Length == 2 && args[0] == "--root" ? Path.GetFullPath(args[1])
    : throw new InvalidOperationException("Use --root REPOSITORY_LOCAL_FIXTURE_DIRECTORY");
if (!requested.StartsWith(project + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
    throw new InvalidOperationException("Host test fixtures must stay beneath this test project");
var work = Path.Combine(requested, "run-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
var passed = new List<string>();
var limits = new List<string>();
Fixture Make(string name) => new(Path.Combine(work, name));
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed.Add(name);
    Console.WriteLine("PASS: " + name);
}
void Reject(Action operation, string name, string? message = null)
{
    Exception? observed = null;
    try { operation(); }
    catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or JsonException or UnauthorizedAccessException)
    { observed = error; }
    Check(observed is not null && (message is null || observed.Message.Contains(message, StringComparison.OrdinalIgnoreCase)), name);
}

var good = Make("standalone");
var context = good.Load();
Check(context.Root == good.Root && context.TaskId == "fixture", "standalone host identity is explicit");
Check(context.ContextPath == "host-context.json" && context.ContextSha256 == Fixture.HashFile(good.ContextPath), "exact context bytes and project-relative path are bound");
Check(context.StateRoot == ".bubo/state/fixture" && context.StagingRoot == ".art-staging", "declared state/staging roots need no OpenCaw installation");
Check(context.Pipeline == "PROOF_TO_MODEL" && context.StyleSha256 == good.PolicyHash, "selected policy fingerprints are data, not interpreted instructions");
Check(context.Engine.Count == 1 && !context.Engine.ContainsKey("definitionSha256") && !context.Engine.ContainsKey("registrySha256") && !context.Engine.ContainsKey("contractSha256"), "minimal generic engine data is preserved without synthesized fingerprints");
Check(context.TimeoutSeconds == 1800 && context.MaxLogBytes == 16777216 && context.MaxPrivateBytes is null, "positive process budgets retain exact values");
Check(!context.AllowProvisioning && !context.AllowPromotion, "production permissions default false");
context.EnsureReadAllowed("inputs/rights.json");
context.EnsureReadAllowed("policy-receipt.json");
context.EnsureReadAllowed("host-context.json");
context.EnsureReadAllowed(".art-staging/future/artifact.glb");
context.EnsureWriteAllowed(".bubo/state/fixture/new/result.json");
context.EnsureWriteAllowed(".art-staging/new/working.blend");
context.EnsureWriteAllowed(".local/tools/new/installation.json");
context.EnsureWriteAllowed("assets/models/new/model.glb");
Check(true, "declared inputs and writable state/staging/tool/output roots are admitted");
var canonicalRights = Path.GetFullPath(Path.Combine(good.Root, "inputs", "rights.json"));
Check(context.ResolveReadPath("inputs/rights.json") == canonicalRights, "read admission returns the canonical checked forward-slash path");
Check(context.ResolveReadPath("inputs\\rights.json") == canonicalRights, "read admission normalizes backslash separators on the current platform");
Check(context.ResolveReadPath(canonicalRights) == canonicalRights, "absolute and relative read admission return the same checked identity");
Check(context.ResolveReadPath("inputs") == Path.GetFullPath(Path.Combine(good.Root, "inputs")), "admitted directory reads return a canonical directory path");
Check(context.ResolveReadPath("policy-receipt.json") == Path.GetFullPath(Path.Combine(good.Root, "policy-receipt.json")), "exact policy references return their canonical paths outside input directories");
var canonicalFutureWrite = Path.GetFullPath(Path.Combine(good.Root, ".bubo", "state", "fixture", "new-output", "result.json"));
var returnedFutureWrite = context.ResolveWritePath(".bubo\\state/fixture\\new-output/result.json");
Check(returnedFutureWrite == canonicalFutureWrite, "write admission normalizes mixed separators and returns the checked full path");
Check(!File.Exists(returnedFutureWrite) && !Directory.Exists(Path.GetDirectoryName(returnedFutureWrite)), "returning an admitted future write path creates no filesystem artifacts");
Reject(() => context.ResolveReadPath("inputs/../policy-receipt.json"), "path-returning read admission still rejects raw traversal");
Reject(() => context.ResolveWritePath("inputs\\rights.json"), "path-returning write admission retains read-only input protection");
Reject(() => context.ResolveWritePath("not-declared/result.json"), "path-returning write admission rejects undeclared output roots");
var futureChild = context.ResolveReadPath("inputs/later-file-parent/child.bin");
good.WriteFile("inputs/later-file-parent", "A previously absent parent is now a file");
Reject(() => context.ResolveReadPath(futureChild), "path-returning admission rechecks changed filesystem ancestor types", "directory");
context.RequireReference("inputs/rights.json", "rights");
Check(true, "a current exact rights reference is available without manufacturing rights");
Reject(() => context.RequireReference("inputs/rights.json", "consent"), "reference kind cannot be relabeled as consent");
Reject(() => context.RequireReference("inputs/unregistered.json"), "unregistered evidence is rejected");
Reject(() => context.EnsureReadAllowed("inputs-neighbor/file"), "read admission uses directory boundaries");
Reject(() => context.EnsureReadAllowed("unregistered.json"), "undeclared project reads are rejected");
Reject(() => context.EnsureWriteAllowed("inputs/new.bin"), "input-only roots are read-only");
Reject(() => context.EnsureWriteAllowed("host-context.json"), "loaded context bytes are read-only");
Reject(() => context.EnsureWriteAllowed("policy-receipt.json"), "registered policy evidence remains read-only");
Reject(() => context.EnsureWriteAllowed("outside/result.json"), "undeclared project writes are rejected");
Reject(() => context.EnsureReadAllowed("."), "whole-project root permission is not implicit");
foreach (var escape in new[] { "../escape", "inputs/../rights.json", "inputs\\..\\rights.json", "inputs/./rights.json", ".git/config", "inputs/.git/config", "inputs/file:stream", "inputs/control\nname" })
    Reject(() => context.EnsureReadAllowed(escape), "path syntax rejected: " + escape.Replace('\n', ' '));
Reject(() => context.EnsureReadAllowed(Path.Combine(work, "sibling/file")), "absolute project escape is rejected");
Reject(() => context.EnsureWriteAllowed(Path.Combine(work, "standalone-neighbor/file")), "sibling project prefix cannot grant write access");
if (OperatingSystem.IsWindows())
{
    foreach (var alias in new[] { "inputs/rights.json.", "inputs/rights.json ", "C:inputs\\rights.json", "\\\\?\\C:\\fixture", "//?/C:/fixture", "inputs/NUL", "inputs/COM1.txt" })
        Reject(() => context.EnsureReadAllowed(alias), "Windows alias rejected: " + alias);
}

var engine = context.Engine;
engine["effectiveEngine"] = "UNKNOWN";
var libraries = context.Libraries;
libraries.Add(new JsonObject { ["id"] = "mutated", ["root"] = "untrusted" });
Check(context.Engine["effectiveEngine"]!.GetValue<string>() == "THREEJS" && context.Libraries.Count == 0, "JSON accessors return isolated clones");
var referenceList = (IList<HostFileReference>)context.References;
var immutable = false;
try { referenceList.Add(new("new", new string('0', 64), "input")); }
catch (NotSupportedException) { immutable = true; }
Check(immutable && context.References.Count == 2, "reference collections cannot mutate admission identities");

var originalRights = File.ReadAllBytes(good.RightsPath);
File.AppendAllText(good.RightsPath, " ");
Reject(context.ValidateFresh, "changed referenced bytes invalidate the loaded context");
Reject(() => context.RequireReference("inputs/rights.json", "rights"), "reference admission hashes current bytes");
File.WriteAllBytes(good.RightsPath, originalRights);
context.ValidateFresh();
Check(true, "exact byte restoration restores reference freshness");
var contextBytes = File.ReadAllBytes(good.ContextPath);
File.AppendAllText(good.ContextPath, "\n");
Reject(context.ValidateFresh, "changed context bytes do not reload expectations");
File.WriteAllBytes(good.ContextPath, contextBytes);
context.ValidateFresh();
Check(true, "exact context byte restoration restores freshness");

void Bad(string name, Action<JsonObject> change, string? expectedMessage = null)
{
    var fixture = Make(name);
    fixture.Write(change);
    Reject(() => fixture.Load(), name, expectedMessage);
}
Bad("unknown-schema", value => value["schemaVersion"] = "bubo-host-context/v2");
Bad("different-project-root", value => value["projectRoot"] = good.Root);
Bad("relative-project-root", value => value["projectRoot"] = "inputs");
Bad("missing-task", value => value.Remove("task"));
Bad("invalid-task", value => value["task"] = "../not-a-task");
Bad("unknown-engine", value => value["policy"]!["engine"]!["effectiveEngine"] = "OTHER");
Bad("unknown-pipeline", value => value["policy"]!["pipeline"] = "OTHER");
Bad("prototype-output-scope", value => value["policy"]!["engine"]!["outputScope"] = "prototypes/new");
foreach (var field in new[] { "definitionSha256", "registrySha256", "contractSha256" })
{
    Bad("invalid-engine-" + field, value => value["policy"]!["engine"]![field] = "not-a-hash");
    Bad("non-string-engine-" + field, value => value["policy"]!["engine"]![field] = 1);
    Bad("uppercase-engine-" + field, value => value["policy"]!["engine"]![field] = new string('A', 64));
    Bad("null-engine-" + field, value => value["policy"]!["engine"]![field] = null);
}
Bad("non-default-null-contract-hash", value => { value["policy"]!["engine"]!["selectionSource"] = "contract"; value["policy"]!["engine"]!["contractSha256"] = null; });
var suppliedFingerprints = Make("provided-engine-fingerprints");
suppliedFingerprints.Write(value =>
{
    value["policy"]!["engine"]!["selectionSource"] = "contract";
    value["policy"]!["engine"]!["definitionSha256"] = suppliedFingerprints.PolicyHash;
    value["policy"]!["engine"]!["registrySha256"] = Fixture.HashFile(suppliedFingerprints.RightsPath);
    value["policy"]!["engine"]!["contractSha256"] = suppliedFingerprints.PolicyHash;
});
var suppliedEngine = JsonNode.Parse(File.ReadAllText(suppliedFingerprints.ContextPath))!["policy"]!["engine"];
Check(JsonNode.DeepEquals(suppliedFingerprints.Load().Engine, suppliedEngine), "valid supplied engine fingerprints are retained exactly");
var defaultFingerprints = Make("default-null-contract-fingerprint");
defaultFingerprints.Write(value =>
{
    value["policy"]!["engine"]!["selectionSource"] = "default";
    value["policy"]!["engine"]!["definitionSha256"] = defaultFingerprints.PolicyHash;
    value["policy"]!["engine"]!["registrySha256"] = Fixture.HashFile(defaultFingerprints.RightsPath);
    value["policy"]!["engine"]!["contractSha256"] = null;
});
var defaultEngine = defaultFingerprints.Load().Engine;
Check(defaultEngine.ContainsKey("contractSha256") && defaultEngine["contractSha256"] is null && defaultEngine["selectionSource"]!.GetValue<string>() == "default", "explicit default selection may retain a null contract fingerprint");
Bad("invalid-policy-hash", value => value["policy"]!["styleSha256"] = "not-a-hash");
Bad("uppercase-policy-hash", value => value["policy"]!["styleSha256"] = new string('A', 64));
Bad("root-cannot-select-project", value => value["roots"]!["state"] = ".");
Bad("root-cannot-be-absolute", value => value["roots"]!["staging"] = good.Root);
Bad("root-cannot-traverse", value => value["roots"]!["state"] = "inputs/../state");
Bad("root-cannot-select-git", value => value["roots"]!["outputs"] = new JsonArray(".git"));
Bad("nested-git-root-rejected", value => value["roots"]!["outputs"] = new JsonArray("outputs/.Git/object"));
Bad("duplicate-input-root", value => value["roots"]!["inputs"] = new JsonArray("inputs", "inputs/"));
Bad("invalid-root-array", value => value["roots"]!["inputs"] = "inputs");
Bad("duplicate-reference", value => value["references"]!.AsArray().Add(value["references"]![0]!.DeepClone()));
Bad("duplicate-normalized-reference", value => { var copy = value["references"]![0]!.DeepClone(); copy["path"] = "inputs\\rights.json"; value["references"]!.AsArray().Add(copy); });
Bad("reference-outside-project", value => value["references"]![0]!["path"] = good.RightsPath);
Bad("reference-context-cycle", value => value["references"]![0]!["path"] = "host-context.json");
Bad("unknown-reference-kind", value => value["references"]![0]!["kind"] = "automatic-approval");
Bad("invalid-reference-hash", value => value["references"]![0]!["sha256"] = "wrong");
Bad("missing-referenced-file", value => value["references"]![0]!["path"] = "inputs/missing.json");
Bad("stale-referenced-file", value => value["references"]![0]!["sha256"] = new string('0', 64));
Bad("missing-timeout", value => value["budgets"]!.AsObject().Remove("timeoutSeconds"));
Bad("zero-timeout", value => value["budgets"]!["timeoutSeconds"] = 0);
Bad("negative-timeout", value => value["budgets"]!["timeoutSeconds"] = -1);
Bad("fractional-timeout", value => value["budgets"]!["timeoutSeconds"] = 1.5);
Bad("overflowing-timeout", value => value["budgets"]!["timeoutSeconds"] = (long)int.MaxValue + 1);
Bad("zero-log-budget", value => value["budgets"]!["maxLogBytes"] = 0);
Bad("overflowing-log-budget", value => value["budgets"]!["maxLogBytes"] = JsonNode.Parse("9223372036854775808"));
Bad("fractional-log-budget", value => value["budgets"]!["maxLogBytes"] = 1.5);
Bad("zero-memory-budget", value => value["budgets"]!["maxPrivateBytes"] = 0);
Bad("negative-memory-budget", value => value["budgets"]!["maxPrivateBytes"] = -1);
Bad("null-memory-budget", value => value["budgets"]!["maxPrivateBytes"] = null);
Bad("string-permission", value => value["permissions"]!["promotion"] = "true");
Bad("null-permissions", value => value["permissions"] = null);
Bad("duplicate-library", value => value["policy"]!["libraries"] = new JsonArray(new JsonObject { ["id"] = "owned", ["root"] = "declared read-only source" }, new JsonObject { ["id"] = "owned", ["root"] = "another source" }));
Bad("malformed-library", value => value["policy"]!["libraries"] = new JsonArray("not-an-object"));

var defaults = Make("optional-arrays-and-permissions");
defaults.Write(value => { value.Remove("permissions"); value.Remove("references"); value.Remove("tools"); value["roots"]!.AsObject().Remove("inputs"); value["roots"]!.AsObject().Remove("outputs"); value["roots"]!.AsObject().Remove("tools"); value["policy"]!.AsObject().Remove("libraries"); value["policy"]!["engine"]!["outputScope"] = null; });
var defaultContext = defaults.Load();
Check(defaultContext.References.Count == 0 && defaultContext.Tools.Count == 0 && defaultContext.Libraries.Count == 0 && !defaultContext.AllowProvisioning && !defaultContext.AllowPromotion, "optional arrays are empty and permissions remain false");
var memory = Make("explicit-positive-memory");
memory.Write(value => { value["budgets"]!["maxPrivateBytes"] = 1073741824L; value["permissions"]!["provisioning"] = true; value["permissions"]!["promotion"] = true; });
var memoryContext = memory.Load();
Check(memoryContext.MaxPrivateBytes == 1073741824 && memoryContext.AllowProvisioning && memoryContext.AllowPromotion, "explicit finite memory and permission values are preserved");

var nestedInput = Make("input-under-staging");
nestedInput.Write(value => value["roots"]!["inputs"] = new JsonArray("inputs", ".art-staging/immutable"));
var nestedContext = nestedInput.Load();
Reject(() => nestedContext.EnsureWriteAllowed(".art-staging/immutable/original.glb"), "input-only subtree remains immutable under broader writable staging");
nestedContext.EnsureWriteAllowed(".art-staging/other/working.glb");
Check(true, "unrelated staging remains writable beside immutable inputs");
var nestedReference = Make("reference-under-state");
nestedReference.WriteFile(".bubo/state/fixture/rights.json", "{\"authorized\":true}");
nestedReference.Write(value => value["references"]!.AsArray().Add(Fixture.Reference(".bubo/state/fixture/rights.json", nestedReference.Root, "rights")));
Reject(() => nestedReference.Load().EnsureWriteAllowed(".bubo/state/fixture/rights.json"), "registered evidence is read-only inside writable state");

var media = Make("media-reference");
media.WriteFile("inputs/media-policy.txt", "opaque caller policy receipt; not interpreted");
media.Write(value => { value["references"]!.AsArray().Add(Fixture.Reference("inputs/media-policy.txt", media.Root, "policy")); value["policy"]!["media"] = new JsonObject { ["path"] = "inputs/media-policy.txt", ["sha256"] = Fixture.HashFile(Path.Combine(media.Root, "inputs/media-policy.txt")) }; });
var mediaContext = media.Load();
Check(mediaContext.MediaPath == "inputs/media-policy.txt" && mediaContext.MediaSha256 == Fixture.HashFile(Path.Combine(media.Root, mediaContext.MediaPath)), "media policy is an exact opaque registered identity");
media.Write(value => value["policy"]!["media"]!["sha256"] = new string('0', 64));
Reject(() => media.Load(), "media hash must match the registered policy reference");
Bad("unregistered-media-policy", value => value["policy"]!["media"] = new JsonObject { ["path"] = "policy-receipt.json", ["sha256"] = new string('0', 64) });

var tool = Make("tools");
tool.WriteFile(".local/tools/synthetic-tool.bin", "CONTROLLED HASH ADMISSION FIXTURE; never executed");
var toolPath = Path.Combine(tool.Root, ".local/tools/synthetic-tool.bin");
tool.Write(value => value["tools"] = new JsonArray(new JsonObject { ["path"] = ".local/tools/synthetic-tool.bin", ["sha256"] = Fixture.HashFile(toolPath) }));
var toolContext = tool.Load();
toolContext.EnsureToolAllowed(toolPath);
Check(Path.IsPathFullyQualified(toolContext.Tools[0].Path) && toolContext.Tools[0].Kind == "tool", "registered tool identity is absolute and omitted kind defaults to tool");
var expectedToolHash = Fixture.HashFile(toolPath);
Check(toolContext.HashToolFile(toolPath) == expectedToolHash, "tool hash admission returns the actual verified digest");
Reject(() => toolContext.EnsureToolAllowed(".local/tools/synthetic-tool.bin"), "native admission requires an absolute selected tool");
Reject(() => toolContext.HashToolFile(".local/tools/synthetic-tool.bin"), "returned tool hashing requires an absolute selected executable");
tool.WriteFile(".local/tools/neighbor.bin", "Unregistered neighbor");
Reject(() => toolContext.EnsureToolAllowed(Path.Combine(tool.Root, ".local/tools/neighbor.bin")), "registered tool does not grant adjacent executable permission");
Reject(() => toolContext.HashToolFile(Path.Combine(tool.Root, ".local/tools/neighbor.bin")), "returned tool hashing rejects an existing unregistered executable", "not registered");
Reject(() => toolContext.EnsureWriteAllowed(toolPath), "registered executable bytes cannot be overwritten through writable tool root");
File.AppendAllText(toolPath, "changed");
Reject(() => toolContext.EnsureToolAllowed(toolPath), "changed executable bytes fail exact tool admission");
Reject(() => toolContext.HashToolFile(toolPath), "stale tool admission cannot return a previously accepted hash", "hash changed");
tool.WriteFile(".local/tools/synthetic-tool.bin", "CONTROLLED HASH ADMISSION FIXTURE; never executed");
toolContext.EnsureToolAllowed(toolPath);
Check(true, "exact tool-byte restoration restores admission");
Check(toolContext.HashToolFile(toolPath) == expectedToolHash, "exact restored tool bytes return the verified pinned digest");
tool.Write(value => value["tools"]!.AsArray().Add(value["tools"]![0]!.DeepClone()));
Reject(() => tool.Load(), "duplicate native tool identities are rejected");
var realTool = Make("existing-executable");
var existingExecutable = Environment.ProcessPath ?? throw new Exception("Test apphost identity unavailable");
realTool.Write(value => value["tools"] = new JsonArray(new JsonObject { ["path"] = existingExecutable, ["sha256"] = Fixture.HashFile(existingExecutable), ["kind"] = "test-apphost" }));
var realToolContext = realTool.Load();
realToolContext.EnsureToolAllowed(existingExecutable);
Check(true, "an explicitly pinned existing external apphost is admitted without copying or executing it");
Check(realToolContext.HashToolFile(existingExecutable) == Fixture.HashFile(existingExecutable), "returned hashing verifies the explicitly registered external apphost bytes");
var changedTool = Make("same-size-tool-content-change");
changedTool.WriteFile("tools/owned-tool.bin", "controlled tool version A");
var changedToolPath = Path.Combine(changedTool.Root, "tools/owned-tool.bin");
changedTool.Write(value => value["tools"] = new JsonArray(new JsonObject { ["path"] = changedToolPath, ["sha256"] = Fixture.HashFile(changedToolPath) }));
var changedToolContext = changedTool.Load();
var pinnedToolTimestamp = File.GetLastWriteTimeUtc(changedToolPath);
changedTool.WriteFile("tools/owned-tool.bin", "controlled tool version B");
File.SetLastWriteTimeUtc(changedToolPath, pinnedToolTimestamp);
Reject(() => changedToolContext.HashToolFile(changedToolPath), "tool hashing rechecks changed bytes at unchanged length and restored timestamp", "hash changed");
Reject(() => changedTool.Load(), "context loading still eagerly rejects changed registered tool bytes", "hash changed");

var production = Make("production-workspace-custom-roots");
production.WriteFile("donor/manifest.json", "{\"kind\":\"immutable controlled donor\"}");
production.WriteFile("donor/audit.jsonl", "{\"event\":\"original donor audit\"}\n");
production.WriteFile("donor/asset.lock", "existing read-only donor lock");
production.WriteFile("inputs/source.bin", "CONTROLLED COPY BYTES; not a model or generated asset");
production.WriteFile("state/current/review-input.json", "{\"kind\":\"immutable supplied review fixture\"}");
production.Write(value =>
{
    value["roots"]!["state"] = "state/current";
    value["roots"]!["staging"] = "working-assets";
    value["roots"]!["tools"] = new JsonArray("tool-cache");
    value["roots"]!["inputs"] = new JsonArray("inputs", "donor");
    value["roots"]!["outputs"] = new JsonArray("runtime-exports");
    value["references"]!.AsArray().Add(Fixture.Reference("state/current/review-input.json", production.Root, "review"));
});
var productionContext = production.Load();
OpenCaw.AssetProduction.IWorkspace productionWorkspace = new OpenCaw.AssetProduction.Workspace(production.Root, productionContext);
Check(productionWorkspace.Root.Equals(productionContext.Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "production workspace root matches its validated host context");
var mismatch = Make("mismatched-production-workspace-root");
Reject(() => new OpenCaw.AssetProduction.Workspace(mismatch.Root, productionContext), "production workspace rejects a different constructor root before admission");
var trailingRootWorkspace = new OpenCaw.AssetProduction.Workspace(production.Root + Path.DirectorySeparatorChar, productionContext);
Check(trailingRootWorkspace.Root.Equals(productionContext.Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "production workspace canonicalizes a matching trailing-separator root");
var donorManifestHash = Fixture.HashFile(Path.Combine(production.Root, "donor/manifest.json"));
var donorAuditHash = Fixture.HashFile(Path.Combine(production.Root, "donor/audit.jsonl"));
var suppliedReviewHash = Fixture.HashFile(Path.Combine(production.Root, "state/current/review-input.json"));
Check(productionWorkspace.Load<JsonObject>("donor/manifest.json")["kind"]!.GetValue<string>() == "immutable controlled donor", "production workspace reads a declared donor without granting write authority");
Reject(() => OpenCaw.AssetProduction.AuditJournal.Append(productionWorkspace, "donor/audit.jsonl", new { eventType = "must-not-append" }), "production audit writer rejects a read-only donor journal");
Check(Fixture.HashFile(Path.Combine(production.Root, "donor/audit.jsonl")) == donorAuditHash, "rejected donor audit append preserves exact original bytes");
Reject(() => OpenCaw.AssetProduction.AuditJournal.Append(productionWorkspace, "inputs/audit-denied/events.jsonl", new { eventType = "must-not-create" }), "production audit admission rejects input-only output before directory creation");
Check(!Directory.Exists(Path.Combine(production.Root, "inputs/audit-denied")), "rejected input audit creates neither directory nor journal");
Reject(() => OpenCaw.AssetProduction.AuditJournal.Append(productionWorkspace, "state/current/review-input.json", new { eventType = "must-not-modify" }), "registered review remains immutable through the production audit writer");
Check(Fixture.HashFile(Path.Combine(production.Root, "state/current/review-input.json")) == suppliedReviewHash, "rejected audit overwrite preserves registered review identity");
Reject(() => productionWorkspace.Save("inputs/save-denied/manifest.json", new { value = "must-not-write" }), "production atomic save rejects input-only output");
Check(!Directory.Exists(Path.Combine(production.Root, "inputs/save-denied")), "denied atomic save creates no directory or temporary candidate");
Reject(() => productionWorkspace.Lock("inputs/lock-denied/asset.lock"), "production writer lock rejects input-only output");
Check(!Directory.Exists(Path.Combine(production.Root, "inputs/lock-denied")), "denied writer lock creates no directory or lock file");
Reject(() => Directory.CreateDirectory(productionWorkspace.ResolveWrite("donor/denied-directory")), "production write resolution blocks directory mutation in donor input");
Check(!Directory.Exists(Path.Combine(production.Root, "donor/denied-directory")), "denied directory mutation leaves donor tree unchanged");
Reject(() => File.Copy(productionWorkspace.Resolve("inputs/source.bin"), productionWorkspace.ResolveWrite("inputs/copy-denied/result.bin")), "production write resolution blocks copied output into input-only roots");
Check(!Directory.Exists(Path.Combine(production.Root, "inputs/copy-denied")), "denied copy creates no input subtree or output bytes");
Reject(() => productionWorkspace.Resolve("inputs/../policy-receipt.json"), "production workspace rejects raw traversal before normalization");
using (var donorReadLock = productionWorkspace.LockRead("donor/asset.lock"))
    Check(donorReadLock.CanRead && !donorReadLock.CanWrite, "read-only donor ownership uses an existing read-only handle");
Reject(() => productionWorkspace.LockRead("donor/missing.lock"), "read-only donor ownership never creates a missing lock");
Check(!File.Exists(Path.Combine(production.Root, "donor/missing.lock")), "missing read-only lock remains absent");
productionWorkspace.Save("state/current/results/manifest.json", new { state = "saved within custom root" });
Check(productionWorkspace.Load<JsonObject>("state/current/results/manifest.json")["state"]!.GetValue<string>() == "saved within custom root", "production atomic persistence honors the custom state root");
OpenCaw.AssetProduction.AuditJournal.Append(productionWorkspace, "state/current/events.jsonl", new { eventType = "admitted-custom-state" });
Check(JsonNode.Parse(File.ReadAllText(Path.Combine(production.Root, "state/current/events.jsonl")))!["eventType"]!.GetValue<string>() == "admitted-custom-state", "production audit append honors the custom state root");
Directory.CreateDirectory(productionWorkspace.ResolveWrite("working-assets/attempt"));
File.WriteAllText(productionWorkspace.ResolveWrite("working-assets/attempt/report.json"), "{\"working\":true}");
Check(productionWorkspace.Load<JsonObject>("working-assets/attempt/report.json")["working"]!.GetValue<bool>(), "production write resolution admits custom staging outputs");
var hashRelative = "working-assets/hash-fixture.bin";
var hashAbsolute = Path.GetFullPath(Path.Combine(production.Root, hashRelative));
const string initialHashBytes = "first controlled hash bytes";
const string changedHashBytes = "other controlled hash bytes";
production.WriteFile(hashRelative, initialHashBytes);
var initialProjectHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(initialHashBytes)));
Check(productionWorkspace.HashProjectFile(hashRelative) == initialProjectHash, "production project-relative hashing admits the declared staging file");
Check(productionWorkspace.HashProjectFile(hashAbsolute) == initialProjectHash, "production absolute project hashing returns the same actual digest");
var originalHashTimestamp = File.GetLastWriteTimeUtc(hashAbsolute);
production.WriteFile(hashRelative, changedHashBytes);
File.SetLastWriteTimeUtc(hashAbsolute, originalHashTimestamp);
var changedProjectHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(changedHashBytes)));
Check(initialHashBytes.Length == changedHashBytes.Length && productionWorkspace.HashProjectFile(hashRelative) == changedProjectHash && changedProjectHash != initialProjectHash, "production project hashing streams changed bytes despite unchanged length and restored timestamp");
Reject(() => productionWorkspace.HashProjectFile("working-assets/../policy-receipt.json"), "production project hashing rejects raw traversal to an otherwise admitted reference", "traversal");
Reject(() => productionWorkspace.HashProjectFile(".git/config"), "production project hashing rejects Git metadata before file access", "Git metadata");
Reject(() => productionWorkspace.HashProjectFile(mismatch.RightsPath), "production project hashing rejects an absolute file in another owned fixture root", "escapes");
production.WriteFile("undeclared-hash-source.bin", "owned bytes outside every declared root/reference");
Reject(() => productionWorkspace.HashProjectFile("undeclared-hash-source.bin"), "production project hashing rejects an existing undeclared project file", "outside the host-declared");
Directory.CreateDirectory(productionWorkspace.ResolveWrite("runtime-exports/version"));
File.Copy(productionWorkspace.Resolve("inputs/source.bin"), productionWorkspace.ResolveWrite("runtime-exports/version/output.bin"));
Check(Fixture.HashFile(Path.Combine(production.Root, "runtime-exports/version/output.bin")) == Fixture.HashFile(Path.Combine(production.Root, "inputs/source.bin")), "production copy preserves exact input bytes in the declared custom output root");
using (productionWorkspace.Lock("state/current/asset.lock"))
    Reject(() => productionWorkspace.Lock("state/current/asset.lock"), "custom-state writer ownership remains exclusive");
Check(Fixture.HashFile(Path.Combine(production.Root, "donor/manifest.json")) == donorManifestHash && Fixture.HashFile(Path.Combine(production.Root, "donor/audit.jsonl")) == donorAuditHash, "production output operations preserve read-only donor identities");
productionContext.ValidateFresh();
Check(true, "admitted production outputs preserve every registered context reference");

var rootFile = Make("directory-root-cannot-be-file");
rootFile.WriteFile("not-a-directory", "owned file used to test a directory root type");
rootFile.Write(value => value["roots"]!["state"] = "not-a-directory");
Reject(() => rootFile.Load(), "declared state directory cannot identify an existing regular file", "directory");
var outputRootFile = Make("directory-array-root-cannot-be-file");
outputRootFile.WriteFile("not-a-directory", "owned file used to test an output directory root type");
outputRootFile.Write(value => value["roots"]!["outputs"] = new JsonArray("not-a-directory"));
Reject(() => outputRootFile.Load(), "declared output directory cannot identify an existing regular file", "directory");
Reject(() => context.EnsureReadAllowed("inputs/rights.json/child.json"), "read admission rejects a child beneath an existing file", "directory");
production.WriteFile("runtime-exports/occupied-file", "existing output file");
Reject(() => productionWorkspace.ResolveWrite("runtime-exports/occupied-file/child.json"), "production write admission rejects a child beneath an existing file", "directory");
Reject(() => HostExecutionContext.Load(good.RightsPath, "host-context.json"), "projectRoot must identify a directory");
Reject(() => HostExecutionContext.Load(good.Root, "inputs"), "context input must identify a file, not a directory");
Bad("reference-cannot-be-directory", value => value["references"]![0]!["path"] = "inputs", "file");
Bad("tool-cannot-be-directory", value => value["tools"] = new JsonArray(new JsonObject { ["path"] = "inputs", ["sha256"] = new string('0', 64) }), "file");

var duplicate = Make("duplicate-json-properties");
var duplicateText = File.ReadAllText(duplicate.ContextPath).Replace("\"task\":\"fixture\"", "\"task\":\"fixture\",\"task\":\"duplicate\"", StringComparison.Ordinal);
File.WriteAllText(duplicate.ContextPath, duplicateText);
Reject(() => duplicate.Load(), "duplicate top-level JSON properties fail before interpretation", "Duplicate");
var nestedDuplicate = Make("duplicate-nested-properties");
File.WriteAllText(nestedDuplicate.ContextPath, File.ReadAllText(nestedDuplicate.ContextPath).Replace("\"effectiveEngine\":\"THREEJS\"", "\"effectiveEngine\":\"THREEJS\",\"effectiveEngine\":\"GODOT\"", StringComparison.Ordinal));
Reject(() => nestedDuplicate.Load(), "duplicate nested JSON properties are rejected", "Duplicate");
var oversized = Make("oversized-context");
File.WriteAllText(oversized.ContextPath, new string(' ', 8 * 1024 * 1024 + 1));
Reject(() => oversized.Load(), "context JSON has an actual 8 MiB byte bound", "8 MiB");
var oversizedReplacement = Make("oversized-context-replacement");
var replacementContext = oversizedReplacement.Load();
File.WriteAllText(oversizedReplacement.ContextPath, new string(' ', 8 * 1024 * 1024 + 1));
Reject(replacementContext.ValidateFresh, "freshness rechecks preserve the context byte bound after replacement", "8 MiB");

if (OperatingSystem.IsWindows())
{
    Bad("case-duplicate-reference", value => { var copy = value["references"]![0]!.DeepClone(); copy["path"] = "INPUTS/RIGHTS.JSON"; value["references"]!.AsArray().Add(copy); });
}
var links = Make("reparse-components");
var target = Path.Combine(links.Root, "target");
Directory.CreateDirectory(target);
File.WriteAllText(Path.Combine(target, "file.txt"), "Owned link target fixture");
var link = Path.Combine(links.Root, "inputs/link");
var created = Fixture.TryDirectoryLink(link, target, out var linkLimit);
if (created)
{
    var linkContext = links.Load();
    Reject(() => linkContext.EnsureReadAllowed("inputs/link/file.txt"), "read admission rejects actual linked path components", "reparse");
    Reject(() => linkContext.EnsureWriteAllowed("inputs/link/new.txt"), "write admission rejects actual linked path components", "reparse");
    Reject(() => HostExecutionContext.Load(link, "host-context.json"), "actual linked project root is rejected", "reparse");
    var dangling = Path.Combine(links.Root, "inputs/dangling");
    if (Fixture.TryDirectoryLink(dangling, Path.Combine(links.Root, "missing-target"), out var danglingLimit))
        Reject(() => linkContext.EnsureReadAllowed("inputs/dangling/file.txt"), "dangling reparse components are rejected", "reparse");
    else limits.Add(danglingLimit);
}
else limits.Add(linkLimit);

var lateLinks = Make("reparse-after-path-admission");
var lateContext = lateLinks.Load();
var lateRelative = ".art-staging/late-link/output.bin";
var formerlyAdmitted = lateContext.ResolveWritePath(lateRelative);
Directory.CreateDirectory(Path.Combine(lateLinks.Root, ".art-staging"));
var lateTarget = Path.Combine(lateLinks.Root, "owned-target");
Directory.CreateDirectory(lateTarget);
if (Fixture.TryDirectoryLink(Path.Combine(lateLinks.Root, ".art-staging/late-link"), lateTarget, out var lateLimit))
{
    Reject(() => lateContext.ResolveWritePath(formerlyAdmitted), "path-returning writes recheck reparse points introduced after prior admission", "reparse");
    Reject(() => lateContext.ResolveReadPath(lateRelative), "path-returning reads reject a newly introduced reparse component", "reparse");
    Check(!File.Exists(Path.Combine(lateTarget, "output.bin")), "failed re-admission leaves the linked target untouched");
}
else limits.Add(lateLimit);

var report = new JsonObject
{
    ["schemaVersion"] = "bubo-host-tests/v1", ["assertions"] = passed.Count,
    ["checks"] = new JsonArray(passed.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
    ["limits"] = new JsonArray(limits.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
    ["scope"] = "Actual filesystem/hash/schema admission only; no native tool execution, generation, rights grant, consent grant or human approval."
};
File.WriteAllText(Path.Combine(work, "summary.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS: {passed.Count} host boundary assertions; report: {Path.Combine(work, "summary.json")}");
foreach (var limit in limits) Console.WriteLine("NOT RUN: " + limit);

sealed class Fixture
{
    public string Root { get; }
    public string ContextPath => Path.Combine(Root, "host-context.json");
    public string RightsPath => Path.Combine(Root, "inputs/rights.json");
    public string PolicyHash => HashFile(Path.Combine(Root, "policy-receipt.json"));
    JsonObject document;
    public Fixture(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
        WriteFile("inputs/rights.json", "{\"authorized\":true,\"reason\":\"SYNTHETIC TEST ONLY; no production rights asserted\"}");
        WriteFile("policy-receipt.json", "{\"kind\":\"opaque fixture policy data\"}");
        document = new JsonObject
        {
            ["schemaVersion"] = "bubo-host-context/v1", ["projectRoot"] = Root, ["task"] = "fixture",
            ["roots"] = new JsonObject { ["state"] = ".bubo/state/fixture", ["staging"] = ".art-staging", ["tools"] = new JsonArray(".local/tools"), ["inputs"] = new JsonArray("inputs"), ["outputs"] = new JsonArray("assets/models") },
            ["policy"] = new JsonObject { ["pipeline"] = "PROOF_TO_MODEL", ["styleSha256"] = PolicyHash, ["engine"] = new JsonObject { ["effectiveEngine"] = "THREEJS" }, ["libraries"] = new JsonArray() },
            ["references"] = new JsonArray(Reference("inputs/rights.json", Root, "rights"), Reference("policy-receipt.json", Root, "policy")),
            ["tools"] = new JsonArray(), ["budgets"] = new JsonObject { ["timeoutSeconds"] = 1800, ["maxLogBytes"] = 16777216 },
            ["permissions"] = new JsonObject { ["provisioning"] = false, ["promotion"] = false }
        };
        Write();
    }
    public void Write(Action<JsonObject>? mutate = null)
    {
        mutate?.Invoke(document);
        File.WriteAllText(ContextPath, document.ToJsonString());
    }
    public void WriteFile(string path, string value)
    {
        var full = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, value);
    }
    public HostExecutionContext Load() => HostExecutionContext.Load(Root, "host-context.json");
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    public static JsonObject Reference(string path, string root, string kind) => new() { ["path"] = path, ["sha256"] = HashFile(Path.Combine(root, path)), ["kind"] = kind };
    public static bool TryDirectoryLink(string link, string target, out string limit)
    {
        link = Path.GetFullPath(link);
        target = Path.GetFullPath(target);
        limit = "";
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A Windows junction is available without symlink privilege. These
            // are new owned fixture paths; no delete/move command is composed.
            if (OperatingSystem.IsWindows() && !link.Concat(target).Any(character => "&|<>^%!\r\n\"".Contains(character)))
            {
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c");
                start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
                start.ArgumentList.Add(link); start.ArgumentList.Add(target);
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode == 0) return true;
                limit = "Actual reparse fixture unavailable: " + stdout.Trim() + " " + stderr.Trim();
                return false;
            }
            limit = "Actual reparse fixture unavailable: " + error.GetType().Name;
            return false;
        }
    }
}
