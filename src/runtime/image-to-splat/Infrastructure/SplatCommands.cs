using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Bubo.Host;
using HostExecutionContext = Bubo.Host.HostExecutionContext;
using Bubo.Splats.Application;
using Bubo.Splats.Domain;
using Hephaera.AssetProduction;

namespace Bubo.Splats.Infrastructure;

public sealed record SplatInspectionRequest
{
    public required string SchemaVersion { get; init; }
    public required string StyleSha256 { get; init; }
    public required SplatSource Input { get; init; }
    public required SplatBudgets Budgets { get; init; }
}

/// <summary>CPU-only, read-only foundation. No installation, subprocess, model import or GPU port.</summary>
public static class SplatCommands
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16, WriteIndented = true
    };

    public static object Execute(Workspace workspace, string operation, string? requestPath,
        AuditedResources resources, CancellationToken cancellation = default)
    {
        var context = workspace.HostContext ?? throw new InvalidOperationException("Validated host context required");
        if (context.InspectionOnly || context.MediaOnly || context.Pipeline != "LOCAL")
            throw new InvalidOperationException("image-to-splat requires explicit LOCAL with full art/engine context");
        context.ValidateFresh();
        object result;
        if (operation == "setup")
        {
            if (requestPath is not null) throw new InvalidOperationException("Setup dry-run does not consume an image request");
            var catalog = resources.Resolve("resources/image-to-splat/catalog.json");
            using var catalogDocument = Parse(File.ReadAllBytes(catalog));
            var document = catalogDocument.RootElement;
            result = new
            {
                schemaVersion = "bubo-splat-setup-plan/v1", mode = "dry-run", status = "blocked",
                catalogSha256 = Workspace.Hash(catalog), catalog = document.Clone(),
                generationAvailable = false, provisioningExecutionAvailable = false,
                gpuUsed = false, filesWritten = false, licenseAcceptancePerformed = false,
                blockers = new[] { "component-code-ancestry-and-notice-review-required", "complete-environment-distribution-review-required",
                    "separate-installation-and-license-evidence-required", "native-Windows-Blackwell-fit-unverified", "generation-worker-not-delivered" }
            };
        }
        else if (operation is "inspect" or "validate")
        {
            if (string.IsNullOrEmpty(requestPath)) throw new InvalidOperationException("Explicit --request required");
            // Only small descriptors are registered host references. Large PLY/image inputs
            // are admitted separately under read roots, with a byte bound before hashing.
            using var requestFile = Open(workspace, requestPath, 65536);
            var bytes = ReadAll(requestFile, 65536, cancellation);
            context.RequireReference(requestPath, "input");
            var relative = workspace.Relative(requestPath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var reference = context.References.SingleOrDefault(r => r.Kind == "input" && r.Path.Equals(relative, comparison))
                ?? throw new InvalidOperationException("Matching descriptor input reference required");
            var requestSha256 = DescriptorIdentity.Verify(bytes, reference.Sha256);
            using var parsed = Parse(bytes);
            if (operation == "validate" && parsed.RootElement.TryGetProperty("schemaVersion", out var schema)
                && schema.ValueKind == JsonValueKind.String && schema.GetString() == "bubo-splat-artifact/v1")
            {
                var artifact = parsed.RootElement.Deserialize<SplatArtifact>(Json) ?? throw new InvalidOperationException("Missing splat artifact");
                SplatRules.ValidateArtifact(artifact);
                MatchStyle(context, artifact.StyleSha256);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(artifact.Budgets.TimeoutSeconds));
                using (var source = Open(workspace, artifact.Input.Path, artifact.Budgets.MaxInputBytes))
                    if (Hash(source, artifact.Budgets.MaxInputBytes, deadline.Token) != artifact.Input.Sha256)
                        throw new InvalidOperationException("Artifact source hash differs from its declaration");
                using var payload = Open(workspace, artifact.Payload.Path, artifact.Budgets.MaxInputBytes);
                var inspection = GaussianInspection.Inspect(payload, artifact.Budgets, artifact.Payload.Sha256, deadline.Token);
                if (inspection.GaussianCount != artifact.GaussianCount || inspection.Profile != artifact.Profile)
                    throw new InvalidOperationException("Artifact declaration differs from inspected Gaussian payload");
                result = new { schemaVersion = "bubo-splat-artifact-validation/v1", valid = true, technicalOnly = true,
                    requestSha256, inspection, ancestryVerified = false, sourceImageDecodingVerified = false,
                    generationAvailable = false, runtimeAcceptanceAvailable = false, gpuUsed = false };
            }
            else if (operation == "validate")
            {
                var request = parsed.RootElement.Deserialize<SplatRequest>(Json) ?? throw new InvalidOperationException("Missing splat request");
                SplatRules.ValidateRequest(request);
                MatchStyle(context, request.StyleSha256);
                using var input = Open(workspace, request.Input.Path, request.Budgets.MaxInputBytes);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(request.Budgets.TimeoutSeconds));
                var digest = Hash(input, request.Budgets.MaxInputBytes, deadline.Token);
                if (digest != request.Input.Sha256) throw new InvalidOperationException("Image input hash differs from request");
                result = new { schemaVersion = "bubo-splat-request-validation/v1", valid = true, technicalOnly = true,
                    requestSha256, sourceSha256 = digest,
                    inputDecodingVerified = false, generationAvailable = false, gpuUsed = false };
            }
            else
            {
                var request = parsed.RootElement.Deserialize<SplatInspectionRequest>(Json) ?? throw new InvalidOperationException("Missing inspection request");
                if (request.SchemaVersion != "bubo-splat-inspection/v1" || request.Input is null || request.Budgets is null)
                    throw new InvalidOperationException("Invalid splat inspection schema");
                MatchStyle(context, request.StyleSha256);
                SplatRules.ValidateBudgets(request.Budgets);
                SplatRules.ValidateSource(request.Input);
                using var input = Open(workspace, request.Input.Path, request.Budgets.MaxInputBytes);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(request.Budgets.TimeoutSeconds));
                result = GaussianInspection.Inspect(input, request.Budgets, request.Input.Sha256, deadline.Token)
                    with { RequestSha256 = requestSha256 };
            }
        }
        else throw new InvalidOperationException("Only inspect, validate and setup dry-run are available");
        cancellation.ThrowIfCancellationRequested();
        context.ValidateFresh();
        cancellation.ThrowIfCancellationRequested();
        return result;
    }

    static void MatchStyle(HostExecutionContext context, string expected)
    {
        if (expected != context.StyleSha256) throw new InvalidOperationException("Splat request STYLE identity differs from host policy");
    }
    static FileStream Open(Workspace workspace, string path, long maximumBytes)
    {
        var full = workspace.Resolve(path);
        var attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
            throw new InvalidOperationException("Splat inputs must be regular repository-local files");
        var length = new FileInfo(full).Length;
        if (length < 1 || length > maximumBytes) throw new InvalidOperationException("Splat input exceeds its finite byte bound");
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != length) { stream.Dispose(); throw new InvalidOperationException("Input changed during admission"); }
        return stream;
    }
    static byte[] ReadAll(FileStream stream, int maximumBytes, CancellationToken cancellation)
    {
        if (stream.Length > maximumBytes) throw new InvalidOperationException("Request exceeds its finite byte bound");
        var bytes = new byte[checked((int)stream.Length)];
        cancellation.ThrowIfCancellationRequested();
        stream.ReadExactly(bytes);
        cancellation.ThrowIfCancellationRequested();
        if (stream.ReadByte() != -1 || stream.Length != bytes.Length) throw new InvalidOperationException("Request changed while reading");
        return bytes;
    }
    static string Hash(FileStream stream, long maximumBytes, CancellationToken cancellation)
    {
        var initial = stream.Length;
        if (initial > maximumBytes) throw new InvalidOperationException("Image exceeds its byte bound");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long count = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = stream.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, initial - count + 1)));
            if (read == 0) break;
            count += read;
            if (count > initial) throw new InvalidOperationException("Image grew while reading");
            hash.AppendData(buffer.AsSpan(0, read));
        }
        if (count != initial || stream.Length != initial) throw new InvalidOperationException("Image changed while reading");
        cancellation.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    static JsonDocument Parse(byte[] bytes)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        try { RejectDuplicates(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidOperationException("Duplicate JSON member");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
