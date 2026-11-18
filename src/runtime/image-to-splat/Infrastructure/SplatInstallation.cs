using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bubo.Host;
using HostExecutionContext = Bubo.Host.HostExecutionContext;
using Bubo.Media.Infrastructure;
using Bubo.Splats.Domain;
using Hephaera.AssetProduction;

namespace Bubo.Splats.Infrastructure;

public sealed record SplatInstallationRequest
{
    public required string SchemaVersion { get; init; }
    public required string Task { get; init; }
    public required string ReceiptPath { get; init; }
    public required string ModelDirectory { get; init; }
    public required string SourceDirectory { get; init; }
    public required string EnvironmentRoot { get; init; }
    public required string PythonBaseRoot { get; init; }
    public required string WheelhouseRoot { get; init; }
    public required string PythonArchive { get; init; }
    public required string NoticeDirectory { get; init; }
    public required SplatSource Python { get; init; }
    public required SplatSource[] Licenses { get; init; }
    public required SplatSource ComponentReview { get; init; }
}

public sealed record PreparedSplatInstallation(string Python, string ModelDirectory, string SourceDirectory,
    string EnvironmentSha256, string CatalogSha256, string ReceiptPath, string ReceiptSha256,
    IReadOnlyList<GpuInputIdentity> Inputs, string[] Roots);

public sealed record SplatInstalledFile
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public required long Bytes { get; init; }
}

public sealed record SplatInstallationReceipt
{
    public required string SchemaVersion { get; init; }
    public required SplatInstallationRequest Registration { get; init; }
    public required string CatalogSha256 { get; init; }
    public required string EnvironmentSpecificationSha256 { get; init; }
    public required string EnvironmentSha256 { get; init; }
    public required SplatInstalledFile[] Files { get; init; }
    public required DateTimeOffset RegisteredAt { get; init; }
}

public sealed record SplatLicenseEvidence
{
    public required string SchemaVersion { get; init; }
    public required string Task { get; init; }
    public required string Backend { get; init; }
    public required bool Accepted { get; init; }
    public required string[] LicenseIds { get; init; }
    public required SplatSource[] Evidence { get; init; }
}

public sealed record SplatComponentReview
{
    public required string SchemaVersion { get; init; }
    public required string Task { get; init; }
    public required string Backend { get; init; }
    public required string CatalogSha256 { get; init; }
    public required string EnvironmentSpecificationSha256 { get; init; }
    public required bool CommercialUseAllowed { get; init; }
    public required bool NoticesVerified { get; init; }
    public required string ProvenanceDisposition { get; init; }
    public required string[] UnresolvedComponents { get; init; }
    public required SplatSource[] Evidence { get; init; }
    public required SplatSource[] NoticeFiles { get; init; }
}

/// <summary>Registers existing exact bytes only. No installer, Python import, model load or GPU operation is available.</summary>
public interface ISplatInstallationVerifier
{
    PreparedSplatInstallation Verify(SplatSource receipt);
}

public sealed class SplatInstallation(Workspace workspace, AuditedResources resources) : ISplatInstallationVerifier
{
    const int MaximumFiles = 65_536;
    const long MaximumBytes = 40L * 1024 * 1024 * 1024;
    static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    HostExecutionContext Host => workspace.HostContext ?? throw new InvalidOperationException("Explicit host context required");

    public object Execute(string requestPath, bool execute, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        AdmitHost();
        var request = SplatJson.Read<SplatInstallationRequest>(workspace, requestPath, "input");
        if (request.Task != Host.TaskId) throw new InvalidOperationException("Installation request belongs to another task");
        var roots = Roots(request, true);
        var (catalog, specification, catalogHash, specificationHash) = Catalog();
        var destination = Host.ResolveWritePath(request.ReceiptPath);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new InvalidOperationException("Installation receipt already exists; retain it and select a fresh identity");
        if (!execute) return new
        {
            schemaVersion = "bubo-splat-installation-plan/v1", mode = "dry-run",
            action = "verify-and-register-existing-installation", catalogSha256 = catalogHash,
            environmentSpecificationSha256 = specificationHash, request.ReceiptPath,
            immutableRoots = roots, python = request.Python, filesWritten = false,
            installation = false, downloads = false, gpuUsed = false, licenseAcceptance = false,
            installationVerified = false, generation = false, promotion = false,
            registrationPlatformSupported = OperatingSystem.IsWindows() && Environment.Is64BitProcess,
            platformRequirement = "existing 64-bit Windows installation; other platforms permit metadata dry-run only",
            requirements = new[] { "existing exact model/source/base/wheel/environment bytes", "current licence and disclosed-provenance review evidence", "separate explicit registration permission" }
        };
        if (!Host.AllowProvisioning) throw new InvalidOperationException("Explicit host provisioning permission required for registration");
        RequirePlatform();
        cancellation.ThrowIfCancellationRequested();
        var inventory = Capture(roots.Append(Host.ResolveReadPath(request.PythonArchive)), cancellation);
        var environmentHash = VerifyContent(request, inventory, catalog, specification, catalogHash, specificationHash, cancellation);
        var receipt = new SplatInstallationReceipt
        {
            SchemaVersion = "bubo-splat-installation/v1", Registration = request,
            CatalogSha256 = catalogHash, EnvironmentSpecificationSha256 = specificationHash,
            EnvironmentSha256 = environmentHash, Files = inventory, RegisteredAt = DateTimeOffset.UtcNow
        };
        cancellation.ThrowIfCancellationRequested(); Host.ValidateFresh();
        SplatJson.WriteNew(workspace, request.ReceiptPath, receipt);
        return new
        {
            schemaVersion = "bubo-splat-installation-result/v1", status = "registered-pending-native-inspection",
            receipt = request.ReceiptPath, sha256 = Hash(Host.ResolveReadPath(request.ReceiptPath), cancellation),
            catalogSha256 = catalogHash, environmentSha256 = environmentHash, files = inventory.Length,
            nativeReadinessVerified = false, ancestryVerified = false, generation = false, promotion = false
        };
    }

    public PreparedSplatInstallation Verify(SplatSource receipt)
    {
        AdmitHost(); RequirePlatform(); BoundReference(receipt, "input");
        var registered = SplatJson.Read<SplatInstallationReceipt>(workspace, receipt.Path, "input", 32 * 1024 * 1024);
        if (registered.SchemaVersion != "bubo-splat-installation/v1" || registered.Registration is null || registered.Files is null)
            throw new InvalidOperationException("Unsupported complete splat installation receipt");
        var request = registered.Registration;
        var roots = Roots(request, false);
        if (!Paths.Equals(workspace.Relative(request.ReceiptPath), workspace.Relative(receipt.Path)))
            throw new InvalidOperationException("Installation receipt cannot be transplanted to another identity");
        var (catalog, specification, catalogHash, specificationHash) = Catalog();
        if (registered.CatalogSha256 != catalogHash || registered.EnvironmentSpecificationSha256 != specificationHash)
            throw new InvalidOperationException("Installation catalog/environment selection changed; preserve the old receipt and explicitly register anew");
        var current = Capture(roots.Append(Host.ResolveReadPath(request.PythonArchive)), CancellationToken.None);
        if (registered.Files.Length != current.Length || !registered.Files.Zip(current).All(pair =>
            pair.First is not null && pair.First.Path == pair.Second.Path && pair.First.Sha256 == pair.Second.Sha256 && pair.First.Bytes == pair.Second.Bytes))
            throw new InvalidOperationException("Installed inventory changed; self-reported versions or receipts do not confer trust");
        var environmentHash = VerifyContent(request, current, catalog, specification, catalogHash, specificationHash, CancellationToken.None);
        if (registered.EnvironmentSha256 != environmentHash) throw new InvalidOperationException("Installed environment identity changed");
        Host.ValidateFresh();
        return new(Host.ResolveReadPath(request.Python.Path), Host.ResolveReadPath(request.ModelDirectory), Host.ResolveReadPath(request.SourceDirectory),
            environmentHash, catalogHash, workspace.Relative(receipt.Path), receipt.Sha256,
            current.Select(file => new GpuInputIdentity(file.Path, file.Sha256)).Append(new(receipt.Path, receipt.Sha256)).ToArray(),
            [.. roots, Host.ResolveReadPath(request.PythonArchive)]);
    }

    void AdmitHost()
    {
        if (Host.InspectionOnly || Host.MediaOnly || Host.Pipeline != "LOCAL" || Host.AllowPromotion)
            throw new InvalidOperationException("Splat registration requires full-art LOCAL without promotion authority");
        Host.ValidateFresh();
    }

    static void RequirePlatform()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Existing splat installation registration initially supports 64-bit Windows only; no fallback");
    }

    string[] Roots(SplatInstallationRequest request, bool writingReceipt)
    {
        if (request.SchemaVersion != "bubo-splat-installation-request/v1" || string.IsNullOrWhiteSpace(request.Task)
            || request.Licenses is not { Length: > 0 and <= 16 } || request.ComponentReview is null || request.Python is null)
            throw new InvalidOperationException("Complete scoped installation request and review references required");
        foreach (var source in request.Licenses.Append(request.ComponentReview).Append(request.Python)) SplatRules.ValidateSource(source);
        var relativeRoots = new[] { request.ModelDirectory, request.SourceDirectory, request.EnvironmentRoot, request.PythonBaseRoot, request.WheelhouseRoot, request.NoticeDirectory };
        foreach (var path in relativeRoots.Append(request.PythonArchive).Append(request.ReceiptPath))
            SplatRules.ValidateSource(new() { Path = path, Sha256 = new string('0', 64) });
        var roots = relativeRoots.Select(Host.ResolveReadPath).ToArray();
        var archive = Host.ResolveReadPath(request.PythonArchive);
        var receipt = writingReceipt ? Host.ResolveWritePath(request.ReceiptPath) : Host.ResolveReadPath(request.ReceiptPath);
        var mutable = new List<string> { Host.ResolveWritePath(Host.StateRoot), Host.ResolveWritePath(Host.StagingRoot) };
        var context = SplatJson.ReadFile<JsonObject>(Path.Combine(Host.Root, Host.ContextPath));
        if (context["roots"]!["outputs"] is JsonArray outputs)
            foreach (var output in outputs) mutable.Add(Host.ResolveWritePath(output!.GetValue<string>()));
        foreach (var root in roots.Append(archive))
        {
            if (!Host.ToolRoots.Any(tool => Within(root, Host.ResolveReadPath(tool))))
                throw new InvalidOperationException("Every immutable dependency must belong to an explicit project tool root");
            if (mutable.Any(path => Overlaps(root, path)) || Overlaps(root, receipt))
                throw new InvalidOperationException("Immutable dependencies cannot overlap state, staging, outputs or the receipt");
        }
        if (roots.Any(first => roots.Count(other => Overlaps(first, other)) != 1) || roots.Any(root => Overlaps(root, archive)))
            throw new InvalidOperationException("Model/source/environment/base/wheel/notice roots and Python archive must be disjoint");
        return roots;
    }

    (JsonObject Catalog, JsonObject Specification, string CatalogHash, string SpecificationHash) Catalog()
    {
        var path = resources.Resolve("resources/image-to-splat/catalog.json");
        var catalog = SplatJson.ReadFile<JsonObject>(path);
        if (catalog["schemaVersion"]?.GetValue<string>() != "bubo-splat-catalog/v1" || catalog["backend"]?.GetValue<string>() != "triposplat")
            throw new InvalidOperationException("Unsupported audited splat catalog");
        var candidate = catalog["environmentCandidates"]!["win-amd64"]!.AsObject();
        var specification = new JsonObject { ["backend"] = "triposplat", ["artifacts"] = candidate["artifacts"]!.DeepClone() };
        return (catalog, specification, resources.Hash("resources/image-to-splat/catalog.json"),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(specification.ToJsonString()))));
    }

    SplatInstalledFile[] Capture(IEnumerable<string> roots, CancellationToken token)
    {
        var files = new List<SplatInstalledFile>(); long total = 0;
        var seen = new HashSet<string>(Paths);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root) && !File.Exists(root)) throw new InvalidOperationException("Existing dependency root/archive missing: " + root);
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var item))
            {
                token.ThrowIfCancellationRequested(); var full = Host.ResolveReadPath(item);
                if (!seen.Add(full)) throw new InvalidOperationException("Aliased dependency inventory path");
                var attributes = File.GetAttributes(full);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) throw new InvalidOperationException("Only regular unlinked installed files/directories are permitted");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (seen.Count > MaximumFiles * 2) throw new InvalidOperationException("Dependency directory inventory exceeds its bound");
                    foreach (var child in Directory.EnumerateFileSystemEntries(full))
                    {
                        if (pending.Count >= MaximumFiles * 2) throw new InvalidOperationException("Dependency directory fan-out exceeds its bound");
                        pending.Push(child);
                    }
                    continue;
                }
                var length = new FileInfo(full).Length; total = checked(total + length);
                if (files.Count >= MaximumFiles || total > MaximumBytes) throw new InvalidOperationException("Installed file/byte inventory exceeds its bound");
                files.Add(new() { Path = workspace.Relative(full), Bytes = length, Sha256 = Hash(full, token, length) });
            }
        }
        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }

    string VerifyContent(SplatInstallationRequest request, SplatInstalledFile[] files, JsonObject catalog, JsonObject specification,
        string catalogHash, string specificationHash, CancellationToken token)
    {
        var byPath = files.ToDictionary(file => file.Path, Paths);
        // Classification uses only this operation's freshly guarded Capture identities.
        // Do not repeat filesystem component walks for pure root membership tests.
        var fullPaths = files.ToDictionary(file => file.Path, file => Path.GetFullPath(file.Path, Host.Root), Paths);
        void ExactRoot(string root, JsonArray expected)
        {
            var fullRoot = Host.ResolveReadPath(root);
            var expectedPaths = new HashSet<string>(Paths);
            foreach (var node in expected)
            {
                var file = node!.AsObject(); var path = workspace.Relative(Path.Combine(fullRoot, file["path"]!.GetValue<string>()));
                if (!expectedPaths.Add(path) || !byPath.TryGetValue(path, out var actual)
                    || actual.Sha256 != file["sha256"]!.GetValue<string>() || actual.Bytes != file["bytes"]!.GetValue<long>())
                    throw new InvalidOperationException("Pinned model/source file differs or is missing: " + path);
            }
            if (!expectedPaths.SetEquals(files.Where(file => Within(fullPaths[file.Path], fullRoot)).Select(file => file.Path)))
                throw new InvalidOperationException("Pinned model/source root contains extra or missing files");
        }
        ExactRoot(request.ModelDirectory, catalog["weights"]!.AsArray());
        ExactRoot(request.SourceDirectory, catalog["source"]!["files"]!.AsArray());
        var review = Rights(request, catalog, catalogHash, specificationHash);
        var noticeRoot = Host.ResolveReadPath(request.NoticeDirectory);
        var declaredNotices = new HashSet<string>(Paths);
        foreach (var notice in review.NoticeFiles)
        {
            SplatRules.ValidateSource(notice); var path = Host.ResolveReadPath(notice.Path);
            if (!Within(path, noticeRoot) || !declaredNotices.Add(workspace.Relative(path))
                || !byPath.TryGetValue(workspace.Relative(path), out var actual) || actual.Sha256 != notice.Sha256)
                throw new InvalidOperationException("Reviewed notice inventory differs from installed bytes");
        }
        if (!declaredNotices.SetEquals(files.Where(file => Within(fullPaths[file.Path], noticeRoot)).Select(file => file.Path)))
            throw new InvalidOperationException("Notices require complete reviewed inventory without extras");
        var media = SplatJson.ReadFile<JsonObject>(resources.Resolve("resources/media/catalog.json"));
        foreach (var node in media["windowsRuntime"]!["notices"]!.AsArray().Concat(catalog["notices"]!.AsArray()))
        {
            var notice = node!.AsObject(); var path = workspace.Relative(Path.Combine(noticeRoot, notice["path"]!.GetValue<string>()));
            if (!byPath.TryGetValue(path, out var actual) || actual.Bytes != notice["bytes"]!.GetValue<long>() || actual.Sha256 != notice["sha256"]!.GetValue<string>())
                throw new InvalidOperationException("Existing pinned Windows runtime notice is missing or changed");
        }
        var runtimeCatalog = new JsonObject { ["windowsRuntime"] = new JsonObject { ["files"] = new JsonArray(media["windowsRuntime"]!["files"]!.AsArray()
            .Where(node => node!["backends"]!.AsArray().Any(backend => backend!.GetValue<string>() == "ace-step-turbo"))
            .Select(node => { var clone = node!.DeepClone().AsObject(); clone["backends"] = new JsonArray("triposplat"); return (JsonNode)clone; }).ToArray()) } };
        var candidate = catalog["environmentCandidates"]!["win-amd64"]!.AsObject();
        var python = Host.ResolveReadPath(request.Python.Path); Host.EnsureToolAllowed(python);
        if (Host.HashToolFile(python) != request.Python.Sha256 || !Paths.Equals(python, Path.Combine(Host.ResolveReadPath(request.EnvironmentRoot), "Scripts", "python.exe")))
            throw new InvalidOperationException("Python must be the exact registered launcher in the dedicated splat environment");
        token.ThrowIfCancellationRequested();
        StandalonePythonIntegrity.Verify(Host, Host.ResolveReadPath(request.EnvironmentRoot), Host.ResolveReadPath(request.PythonBaseRoot),
            Host.ResolveReadPath(request.WheelhouseRoot), Host.ResolveReadPath(request.PythonArchive), specification,
            candidate["python"]!.AsObject(), runtimeCatalog);
        token.ThrowIfCancellationRequested();
        var environmentRoot = Host.ResolveReadPath(request.EnvironmentRoot);
        var pythonBaseRoot = Host.ResolveReadPath(request.PythonBaseRoot);
        var environmentFiles = files.Where(file => Within(fullPaths[file.Path], environmentRoot)
            || Within(fullPaths[file.Path], pythonBaseRoot));
        var text = string.Concat(environmentFiles.OrderBy(file => file.Path, StringComparer.Ordinal).Select(file =>
            file.Path + "|" + file.Sha256 + "|" + file.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    SplatComponentReview Rights(SplatInstallationRequest request, JsonObject catalog, string catalogHash, string specificationHash)
    {
        var accepted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in request.Licenses)
        {
            BoundReference(identity, "rights"); var license = SplatJson.Read<SplatLicenseEvidence>(workspace, identity.Path, "rights");
            if (license.SchemaVersion != "bubo-splat-license-evidence/v1" || license.Task != request.Task || license.Backend != "triposplat" || !license.Accepted
                || license.LicenseIds is not { Length: > 0 and <= 64 } || license.LicenseIds.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("Current explicit applicable licence evidence required; registration accepts no terms");
            Evidence(license.Evidence); foreach (var id in license.LicenseIds) accepted.Add(id);
        }
        foreach (var component in catalog["licenseComponents"]!.AsArray())
            if (!accepted.Contains(component!["id"]!.GetValue<string>())) throw new InvalidOperationException("A selected component licence lacks evidence");
        BoundReference(request.ComponentReview, "rights");
        var review = SplatJson.Read<SplatComponentReview>(workspace, request.ComponentReview.Path, "rights");
        if (review.SchemaVersion != "bubo-splat-component-review/v1" || review.Task != request.Task || review.Backend != "triposplat"
            || review.CatalogSha256 != catalogHash || review.EnvironmentSpecificationSha256 != specificationHash
            || !review.CommercialUseAllowed || !review.NoticesVerified || review.ProvenanceDisposition != "accepted-disclosed-derivative-lineage"
            || review.UnresolvedComponents is null || !review.UnresolvedComponents.SequenceEqual(["dinov3-conversion"])
            || review.NoticeFiles is not { Length: > 0 and <= 1024 })
            throw new InvalidOperationException("Exact current component, notice and disclosed DINOv3 derivative-lineage review required; no independent ancestry claim is inferred");
        Evidence(review.Evidence); return review;
    }

    void Evidence(SplatSource[] evidence)
    {
        if (evidence is not { Length: > 0 and <= 64 }) throw new InvalidOperationException("Existing hash-bound human/entity source evidence is required");
        foreach (var identity in evidence) BoundReference(identity, "rights");
    }

    void BoundReference(SplatSource source, string kind)
    {
        SplatRules.ValidateSource(source); Host.RequireReference(source.Path, kind); var relative = workspace.Relative(source.Path);
        if (!Host.References.Any(reference => reference.Kind == kind && Paths.Equals(reference.Path, relative) && reference.Sha256 == source.Sha256))
            throw new InvalidOperationException("Declared evidence differs from its exact registered identity");
    }

    static bool Within(string path, string root)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return Paths.Equals(path, root) || path.StartsWith(root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    static bool Overlaps(string first, string second) => Within(first, second) || Within(second, first);
    static string Hash(string path, CancellationToken token, long? expectedBytes = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = stream.Length;
        if (expectedBytes is { } expected && length != expected) throw new InvalidOperationException("Dependency length changed before hashing");
        long count = 0; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65_536];
        while (true)
        {
            token.ThrowIfCancellationRequested(); var read = stream.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - count + 1)));
            if (read == 0) break; count += read;
            if (count > length) throw new InvalidOperationException("Dependency grew while hashing");
            hash.AppendData(buffer.AsSpan(0, read));
        }
        if (count != length || stream.Length != length) throw new InvalidOperationException("Dependency changed while hashing");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
