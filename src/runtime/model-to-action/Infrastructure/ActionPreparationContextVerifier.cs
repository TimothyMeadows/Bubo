using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HostExecutionContext = Bubo.Host.HostExecutionContext;
using Bubo.Vfx.Domain;
using Bubo.Vfx.Infrastructure;
using Hephaera.AssetProduction;
using Hephaera.ModelAction.Application;
using Hephaera.ModelAction.Domain;
using static Hephaera.ModelAction.Application.ActionEffectDependencies;
using static Hephaera.ModelAction.Application.ActionValidation;

namespace Hephaera.ModelAction.Infrastructure;

public sealed record ActionPreparationVerification(VfxPreparationResult Result,
    IReadOnlyList<ActionEffectFile> SourceFiles, IReadOnlyList<ActionSizedFileReference> OriginalSources,
    VfxPackage PreparedPackage);

/// <summary>
/// Reopens an explicitly supplied original task context under the intersection of
/// current and original read scopes. It never changes a preparation identity or grants writes.
/// </summary>
public static class ActionPreparationContextVerifier
{
    static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static ActionPreparationVerification Verify(IWorkspace current, ActionEffectSource source,
        ActionEffectInspectionContext context, Action<ActionEffectFile> observe, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(observe);
        var preparation = source.Preparation ?? throw new InvalidOperationException("Preparation context requires an exact native preparation seal");
        Hash(context.RuntimeBuildSha256); Hash(context.StyleSha256);
        var currentHost = current.HostContext;
        currentHost?.ValidateFresh();
        if (source.PreparationContext is not null && currentHost is null)
            throw new InvalidOperationException("Explicit preparation context requires a validated current host context");
        var files = new Dictionary<string, ActionEffectFile>(StringComparer.OrdinalIgnoreCase);
        using var leases = new ReadLeases();

        ActionEffectFile Register(string role, string path, string sha256, long? declaredBytes = null, bool json = false)
        {
            cancellation.ThrowIfCancellationRequested(); Hash(sha256);
            var full = Resolve(current, path);
            var bytes = new FileInfo(full).Length;
            if (bytes < 1 || bytes > (json ? EffectProfileV2.MaximumJsonBytes : MaximumFileBytes) || declaredBytes.HasValue && bytes != declaredBytes.Value)
                throw new InvalidOperationException("Preparation ancestry requires a matching finite bounded file length");
            if (files.TryGetValue(path, out var previous))
            {
                if (previous.Path != path || previous.Sha256 != sha256 || previous.Bytes != bytes)
                    throw new InvalidOperationException("Preparation ancestry aliases conflicting identities");
                return previous;
            }
            var file = new ActionEffectFile { Role = role, SourceId = source.Id, Path = path, Sha256 = sha256, Bytes = bytes };
            // The owning inspector reserves its remaining cumulative work before any payload is read here.
            observe(file);
            leases.Add(full, bytes);
            var checkedFile = Observe(current, path, bytes, cancellation);
            if (checkedFile.Bytes != bytes || checkedFile.Sha256 != sha256)
                throw new InvalidOperationException("Preparation ancestry does not match the exact admitted bytes");
            files.Add(path, file);
            return file;
        }

        byte[] Payload(ActionEffectFile file)
        {
            var bytes = Read(current, file.Path, EffectProfileV2.MaximumJsonBytes, cancellation);
            if (bytes.LongLength != file.Bytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256)
                throw new InvalidOperationException("Preparation ancestry changed after bounded admission");
            return bytes;
        }

        var sealFile = Register("native-preparation-seal", preparation.Path, preparation.Sha256, json: true);
        var seal = EffectProfileV2.ParseData<VfxPreparationSeal>(Payload(sealFile));
        if (seal.SchemaVersion != "bubo-vfx-preparation-result/v1" || seal.BuildSha256 != context.RuntimeBuildSha256
            || !preparation.Path.EndsWith("/preparation.json", StringComparison.Ordinal) || seal.PackagePath != "package.json")
            throw new InvalidOperationException("Preparation seal schema, current build or original path is invalid");
        Id(seal.TaskId); Id(seal.Id); Id(seal.Version); Hash(seal.PolicySha256); Hash(seal.RequestSha256);
        var output = preparation.Path[..^"/preparation.json".Length];
        if (seal.Files is null || seal.Files.Count is < 4 or > 260)
            throw new InvalidOperationException("Preparation seal has no finite native inventory");
        var inventory = new Dictionary<string, ActionEffectFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in seal.Files)
        {
            if (file is null || inventory.ContainsKey(file.Path)) throw new InvalidOperationException("Preparation inventory has duplicate or missing entries");
            var observed = Register("native-preparation-file", output + "/" + file.Path, file.Sha256, file.Bytes,
                file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            inventory.Add(file.Path, observed);
        }
        if (!inventory.TryGetValue("request.json", out var snapshot) || snapshot.Sha256 != seal.RequestSha256
            || !inventory.TryGetValue("started.json", out var startFile) || !inventory.ContainsKey("package.json"))
            throw new InvalidOperationException("Preparation seal lacks exact request/start/package ancestry");
        var request = PreparationJson.Parse(Payload(snapshot));
        if (!PreparationValidation.Validate(request).IsValid || request.Id != seal.Id || request.Version != seal.Version)
            throw new InvalidOperationException("Preparation request does not match its valid native seal");
        Register("native-preparation-request", seal.RequestPath, seal.RequestSha256, snapshot.Bytes, true);
        var originals = new List<ActionSizedFileReference>();
        foreach (var original in request.Sources)
        {
            Register("native-preparation-source", original.Path, original.Sha256, original.Bytes);
            originals.Add(new() { Path = original.Path, Sha256 = original.Sha256, Bytes = original.Bytes });
        }

        JsonObject? originValue = null;
        ActionEffectFile? originFile = null;
        var state = currentHost?.StateRoot ?? ".bubo/state";
        if (source.PreparationContext is { } originReference)
        {
            currentHost!.RequireReference(originReference.Path, "input");
            originFile = Register("native-preparation-context", originReference.Path, originReference.Sha256, json: true);
            originValue = EffectProfileV2.ParseData<JsonObject>(Payload(originFile));
            Fields(originValue, "schemaVersion", "projectRoot", "task", "roots", "policy", "references", "tools", "budgets", "permissions");
            if (Text(originValue, "schemaVersion") != "bubo-host-context/v1" || Text(originValue, "task") != seal.TaskId
                || !Path.TrimEndingDirectorySeparator(Path.GetFullPath(Text(originValue, "projectRoot"))).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(current.Root)), Comparison))
                throw new InvalidOperationException("Preparation context must preserve the original task in the same project");
            var permissions = originValue["permissions"]?.AsObject() ?? throw new InvalidOperationException("Preparation verification context needs explicit false permissions");
            Fields(permissions, "provisioning", "promotion");
            if (permissions["provisioning"]?.GetValue<bool>() != false || permissions["promotion"]?.GetValue<bool>() != false
                || Array(originValue, "tools").Count != 0)
                throw new InvalidOperationException("Preparation verification context cannot carry tools, provisioning or promotion permissions");
            var roots = originValue["roots"]?.AsObject() ?? throw new InvalidOperationException("Preparation verification context needs original roots");
            Fields(roots, "state", "staging", "tools", "inputs", "outputs", "mediaState");
            if (Array(roots, "tools").Count != 0) throw new InvalidOperationException("Preparation verification context cannot declare tool roots");
            state = Text(roots, "state");
            // Even directory declarations must be within the current caller's read scope.
            foreach (var field in new[] { "state", "staging", "mediaState" })
                if (roots[field] is not null) _ = Resolve(current, Text(roots, field));
            foreach (var field in new[] { "inputs", "outputs" })
                foreach (var root in Array(roots, field)) _ = Resolve(current, root?.GetValue<string>() ?? throw new InvalidOperationException("Missing original root"));
            var policy = originValue["policy"]?.AsObject() ?? throw new InvalidOperationException("Preparation verification context needs the original policy");
            if (Text(policy, "pipeline") != currentHost.Pipeline || Text(policy, "styleSha256") != currentHost.StyleSha256
                || !JsonNode.DeepEquals(policy["engine"], currentHost.Engine)
                || !JsonNode.DeepEquals(policy["libraries"] ?? new JsonArray(), currentHost.Libraries))
                throw new InvalidOperationException("Preparation context engine, STYLE, pipeline or library policy differs from the current caller");
            var references = Array(originValue, "references");
            if (references.Count > 4096) throw new InvalidOperationException("Preparation context reference inventory is oversized");
            foreach (var entry in references)
            {
                var reference = entry?.AsObject() ?? throw new InvalidOperationException("Missing preparation context reference");
                Fields(reference, "path", "sha256", "kind");
                var path = Text(reference, "path");
                if (path.Equals(source.ProductionReview.Path, Comparison))
                    throw new InvalidOperationException("Preparation context cannot hash-reference its dependent source production review");
                var kind = Text(reference, "kind");
                if (kind is not ("input" or "policy" or "rights" or "review" or "consent")) throw new InvalidOperationException("Unknown preparation context reference kind");
                Register("native-preparation-context-reference", path, Text(reference, "sha256"), json: path.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            }
        }
        else if (seal.TaskId != (currentHost?.TaskId ?? "compiled-caller"))
            throw new InvalidOperationException("Cross-task preparation reuse requires an explicit original read-only preparation context");

        // The original claim path is derived only from the supplied original state and exact sealed IDs.
        var claim = state + "/vfx-preparation/" + request.Id + "/" + request.Version + ".json";
        Register("native-preparation-claim", claim, startFile.Sha256, startFile.Bytes, true);
        var start = EffectProfileV2.ParseData<JsonObject>(Payload(startFile));
        if (Text(start, "taskId") != seal.TaskId || Text(start, "buildSha256") != context.RuntimeBuildSha256
            || Text(start, "policySha256") != seal.PolicySha256 || Text(start, "outputPath") != output
            || Text(start, "requestSha256") != seal.RequestSha256)
            throw new InvalidOperationException("Preparation original start and claim identity are inconsistent");

        HostExecutionContext? origin = currentHost;
        if (originFile is not null)
        {
            // Every file HostExecutionContext.Load may hash was already admitted by current scope and cumulative budgets.
            origin = HostExecutionContext.Load(current.Root, originFile.Path);
            if (origin.ContextSha256 != originFile.Sha256 || origin.TaskId != seal.TaskId || origin.StateRoot != state
                || origin.AllowProvisioning || origin.AllowPromotion || origin.Tools.Count != 0 || origin.ToolRoots.Count != 0
                || origin.InspectionOnly || origin.MediaOnly)
                throw new InvalidOperationException("Original preparation context changed or carries unsupported authority");
        }
        if (origin is not null)
        {
            if (origin.StyleSha256 != context.StyleSha256 || !JsonNode.DeepEquals(origin.Engine, context.Engine)
                || currentHost is null || HostPolicyIdentity.Fingerprint(origin) != HostPolicyIdentity.Fingerprint(currentHost)
                || HostPolicyIdentity.Fingerprint(origin) != seal.PolicySha256)
                throw new InvalidOperationException("Preparation seal/original/current selected policy identities differ");
        }
        var readOnly = new ReadOnlyPreparationWorkspace(current, origin, files.Values.Select(file => file.Path));
        var verified = VfxPreparation.Verify(readOnly, preparation.Path, context.RuntimeBuildSha256, cancellation);
        if (!verified.IsValid || verified.ManifestSha256 != preparation.Sha256 || verified.PackagePath != output + "/package.json")
            throw new InvalidOperationException("Native preparation verification returned different original ancestry");
        var prepared = VfxJson.Parse(Read(readOnly, verified.PackagePath, VfxJson.MaximumPackageBytes, cancellation));
        RequireEngine(prepared, context.Engine, context.StyleSha256);
        foreach (var file in files.Values)
        {
            var fresh = Observe(current, file.Path, file.Bytes, cancellation);
            if (fresh.Bytes != file.Bytes || fresh.Sha256 != file.Sha256) throw new InvalidOperationException("Preparation context/source changed during verification");
        }
        currentHost?.ValidateFresh();
        return new(verified, files.Values.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(), originals.AsReadOnly(), prepared);
    }

    sealed class ReadLeases : IDisposable
    {
        readonly List<FileStream> files = [];
        public void Add(string path, long expectedBytes)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!stream.CanSeek || stream.Length != expectedBytes)
            {
                stream.Dispose();
                throw new InvalidOperationException("Preparation input changed before its read-only lease");
            }
            files.Add(stream);
        }
        public void Dispose() { foreach (var stream in files) stream.Dispose(); }
    }

    sealed class ReadOnlyPreparationWorkspace : IWorkspace
    {
        readonly IWorkspace current;
        readonly HostExecutionContext? origin;
        readonly HashSet<string> paths;
        readonly HashSet<string> directories;
        public ReadOnlyPreparationWorkspace(IWorkspace current, HostExecutionContext? origin, IEnumerable<string> files)
        {
            this.current = current; this.origin = origin;
            paths = new(files, StringComparer.Ordinal);
            directories = new(StringComparer.Ordinal);
            foreach (var file in paths)
            {
                var parent = Path.GetDirectoryName(file)?.Replace('\\', '/');
                while (!string.IsNullOrEmpty(parent)) { directories.Add(parent); parent = Path.GetDirectoryName(parent)?.Replace('\\', '/'); }
            }
        }
        public string Root => current.Root;
        public HostExecutionContext? HostContext => origin;
        public string Resolve(string path)
        {
            var relative = current.Relative(path);
            if (!paths.Contains(relative) && !directories.Contains(relative)) throw new InvalidOperationException("Preparation verifier attempted an unregistered read");
            var full = current.Resolve(relative);
            if (origin is not null && !origin.ResolveReadPath(relative).Equals(full, Comparison))
                throw new InvalidOperationException("Current and original preparation scopes resolve different files");
            return full;
        }
        public string Relative(string path) => Path.GetRelativePath(Root, Resolve(path)).Replace('\\', '/');
        public string HashFile(string absolutePath) => current.HashProjectFile(Relative(absolutePath));
        public string HashProjectFile(string path) => current.HashProjectFile(Relative(path));
        public T Load<T>(string path) => JsonSerializer.Deserialize<T>(Read(this, Relative(path), EffectProfileV2.MaximumJsonBytes, default), Workspace.Json)
            ?? throw new InvalidOperationException("Missing preparation source JSON");
        public string ResolveWrite(string path) => throw new InvalidOperationException("Preparation verification workspace is read-only");
        public void Save<T>(string path, T value) => throw new InvalidOperationException("Preparation verification workspace is read-only");
        public FileStream Lock(string path) => throw new InvalidOperationException("Preparation verification workspace cannot acquire write locks");
        public FileStream LockRead(string path) => throw new InvalidOperationException("Preparation verification workspace cannot acquire locks");
    }
}
