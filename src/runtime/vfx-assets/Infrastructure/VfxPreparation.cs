using Bubo.Vfx.Application;
using Bubo.Vfx.Domain;
using Hephaera.AssetProduction;
using static Bubo.Vfx.Infrastructure.VfxPreparationFiles;

namespace Bubo.Vfx.Infrastructure;

public sealed record VfxPreparedFile
{
    public required string Path { get; init; }
    public required long Bytes { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record VfxPreparationSeal
{
    public required string SchemaVersion { get; init; }
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string TaskId { get; init; }
    public required string BuildSha256 { get; init; }
    public required string PolicySha256 { get; init; }
    public required string RequestPath { get; init; }
    public required string RequestSha256 { get; init; }
    public required string PackagePath { get; init; }
    public required IReadOnlyList<VfxPreparedFile> Files { get; init; }
}

public sealed record VfxPreparationResult(string ManifestPath, string ManifestSha256, string PackagePath,
    string PackageSha256, IReadOnlyList<VfxPreparedFile> Files, string LoopIntent,
    bool IsValid = true, bool HumanAcceptance = false, bool PromotionAvailable = false,
    string LoopQuality = "not-established-by-pixel-or-endpoint-checks",
    string MemoryEnforcement = "bounded-explicit-working-allocations-not-hard-process-memory");

internal sealed record VfxPreparationStart
{
    public required string SchemaVersion { get; init; }
    public required int ProcessId { get; init; }
    public required string StartedAt { get; init; }
    public required string OutputPath { get; init; }
    public required string RequestSha256 { get; init; }
    public required string BuildSha256 { get; init; }
    public required string PolicySha256 { get; init; }
    public required string TaskId { get; init; }
}

/// <summary>Immutable fixed-input preparation. Existing attempts, including failures, are never retried or overwritten.</summary>
public static class VfxPreparation
{
    // Request/receipt JSON, parsed objects, digest buffers and file-inventory metadata.
    // This conservative reservation is separate from the pixel port's live-buffer bound.
    const long HostWorkingReservation = 16 * 1024 * 1024;
    public static VfxPreparationResult Prepare(IWorkspace workspace, string requestPath, string outputPath,
        string buildSha256, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var requestFull = Resolve(workspace, requestPath);
        var requestBytes = Read(requestFull, PreparationJson.MaximumRequestBytes, cancellation);
        var request = PreparationJson.Parse(requestBytes);
        Admit(workspace, requestPath, request, buildSha256, cancellation);
        using var deadline = Deadline(workspace, request, cancellation);
        var token = deadline.Token;
        var output = Output(workspace, outputPath);
        if (File.Exists(output) || Directory.Exists(output))
            throw new InvalidOperationException("Preparation output already exists; retain the attempt and choose a new explicit version/output");
        if (Within(requestFull, output) || requestFull.Equals(output, Comparison))
            throw new InvalidOperationException("Preparation output overlaps its request");
        foreach (var source in request.Sources)
        {
            var full = Resolve(workspace, source.Path);
            if (Within(full, output) || full.Equals(output, Comparison))
                throw new InvalidOperationException("Preparation output overlaps its original inputs");
            Match(full, source.Bytes, source.Sha256, request.Budgets.MaxSourceBytes, token);
        }
        if (request.Sources.Count + 6 > request.Budgets.MaxOutputFiles)
            throw new InvalidOperationException("Preparation output file budget cannot hold immutable source snapshots and receipts");
        if (request.Budgets.MaxWorkingBytes < HostWorkingReservation + RgbaPngCodec.FixedWorkingBytes)
            throw new InvalidOperationException("Preparation working allocation budget cannot hold bounded request/receipt and IO buffers");
        var requiredBytes = checked(request.Sources.Sum(s => s.Bytes) + requestBytes.LongLength);
        if (requiredBytes >= request.Budgets.MaxOutputBytes)
            throw new InvalidOperationException("Preparation output budget cannot hold its immutable source and request snapshots");
        FreeDisk(output, request.Budgets.MinimumFreeDiskBytes, request.Budgets.MaxOutputBytes);
        FreshOriginals(workspace, requestPath, Hash(requestBytes), request, token);
        // CreateNew is the one-winner claim if two callers race to create the absent directory.
        var start = new VfxPreparationStart
        {
            SchemaVersion = "bubo-vfx-preparation-start/v1", ProcessId = Environment.ProcessId,
            StartedAt = DateTimeOffset.UtcNow.ToString("O"), OutputPath = outputPath,
            RequestSha256 = Hash(requestBytes), BuildSha256 = buildSha256,
            PolicySha256 = Policy(workspace, request), TaskId = workspace.HostContext?.TaskId ?? "compiled-caller"
        };
        var files = new List<VfxPreparedFile>();
        var startBytes = Encode(start);
        if (checked(requiredBytes + startBytes.LongLength * 2) >= request.Budgets.MaxOutputBytes)
            throw new InvalidOperationException("Preparation output budget cannot hold its permanent claim, start receipt and immutable inputs");
        var claim = Resolve(workspace, ClaimPath(workspace, request), true);
        Directory.CreateDirectory(Path.GetDirectoryName(claim)!);
        // Permanent one-winner version reservation, independent of caller-selected output path.
        // A failed/interrupted version stays spent; a new version never inherits its seal.
        Create(claim, startBytes, token);
        Directory.CreateDirectory(output);
        long outputBytes = startBytes.LongLength;
        void Save(string relative, byte[] bytes)
        {
            if (bytes.LongLength > request.Budgets.MaxOutputBytes - outputBytes)
                throw new InvalidOperationException("Preparation cumulative output byte budget exceeded");
            var full = Resolve(workspace, outputPath + "/" + relative, true);
            Create(full, bytes, token);
            outputBytes = checked(outputBytes + bytes.LongLength);
            files.Add(new() { Path = relative, Bytes = bytes.LongLength, Sha256 = Hash(bytes) });
        }
        Save("started.json", startBytes);
        Save("request.json", requestBytes);
        if (request.Sources.Count > 0) Directory.CreateDirectory(Resolve(workspace, outputPath + "/sources", true));
        for (var index = 0; index < request.Sources.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var source = request.Sources[index];
            var relative = Snapshot(index);
            var target = Resolve(workspace, outputPath + "/" + relative, true);
            using var input = Open(Resolve(workspace, source.Path), request.Budgets.MaxSourceBytes, source.Bytes);
            using var writer = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536);
            var buffer = new byte[65536];
            long copied = 0;
            for (;;)
            {
                token.ThrowIfCancellationRequested();
                var count = input.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, source.Bytes - copied + 1)));
                if (count == 0) break;
                copied = checked(copied + count);
                if (copied > source.Bytes || count > request.Budgets.MaxOutputBytes - outputBytes)
                    throw new InvalidOperationException("Preparation source grew or exceeded the cumulative output budget");
                writer.Write(buffer, 0, count);
                outputBytes += count;
            }
            writer.Flush(true);
            writer.Dispose();
            Match(target, source.Bytes, source.Sha256, request.Budgets.MaxSourceBytes, token);
            files.Add(new() { Path = relative, Bytes = copied, Sha256 = source.Sha256 });
        }
        var pixels = Render(workspace, request, outputPath, token);
        var atlasPath = Resolve(workspace, outputPath + "/atlas.png", true);
        using (var writer = new FileStream(atlasPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var limits = OutputLimits(request) with { MaxOutputBytes = Math.Min(134217728, request.Budgets.MaxOutputBytes - outputBytes) };
            RgbaPngCodec.Write(writer, pixels.Image, limits, token);
            writer.Flush(true);
        }
        var atlas = Observe(atlasPath, "atlas.png", request.Budgets.MaxOutputBytes - outputBytes, token);
        outputBytes += atlas.Bytes;
        files.Add(atlas);
        var packageBytes = Package(request, pixels, atlas);
        Save("package.json", packageBytes);
        if (!VfxValidation.Validate(VfxJson.Parse(packageBytes)).IsValid)
            throw new InvalidOperationException("Prepared package failed native contract validation");
        VerifyPixels(workspace, request, outputPath, pixels.Image, token);
        foreach (var file in files) Match(Resolve(workspace, outputPath + "/" + file.Path), file.Bytes, file.Sha256, request.Budgets.MaxOutputBytes, token);
        CheckInventory(workspace, outputPath, files.Select(f => f.Path), token);
        FreshOriginals(workspace, requestPath, start.RequestSha256, request, token);
        Match(claim, startBytes.Length, Hash(startBytes), JsonLimit, token);
        var seal = new VfxPreparationSeal
        {
            SchemaVersion = "bubo-vfx-preparation-result/v1", Id = request.Id, Version = request.Version,
            TaskId = start.TaskId, BuildSha256 = buildSha256, PolicySha256 = start.PolicySha256,
            RequestPath = requestPath, RequestSha256 = start.RequestSha256, PackagePath = "package.json",
            Files = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray()
        };
        var sealBytes = Encode(seal);
        if (sealBytes.Length > JsonLimit || sealBytes.Length > request.Budgets.MaxOutputBytes - outputBytes)
            throw new InvalidOperationException("Preparation seal exceeds JSON or cumulative output byte budget");
        FreeDisk(output, request.Budgets.MinimumFreeDiskBytes, sealBytes.Length);
        // The durable seal is the commit point. A cancellation arriving after this
        // exclusive flushed write does not retroactively turn a committed result into failure.
        Create(Resolve(workspace, outputPath + "/preparation.json", true), sealBytes, token);
        return new(outputPath + "/preparation.json", Hash(sealBytes), outputPath + "/package.json", Hash(packageBytes),
            seal.Files, request.Timing.Loop);
    }

    public static VfxPreparationResult Verify(IWorkspace workspace, string manifestPath,
        string buildSha256, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Canonical(manifestPath);
        if (!manifestPath.EndsWith("/preparation.json", StringComparison.Ordinal))
            throw new InvalidOperationException("Preparation verification requires the canonical sealed preparation.json");
        var outputPath = manifestPath[..^"/preparation.json".Length];
        var output = Output(workspace, outputPath);
        var manifestBytes = Read(Resolve(workspace, manifestPath), JsonLimit, cancellation);
        var seal = Parse<VfxPreparationSeal>(manifestBytes);
        var requestBytes = Read(Resolve(workspace, outputPath + "/request.json"), PreparationJson.MaximumRequestBytes, cancellation);
        var request = PreparationJson.Parse(requestBytes);
        Admit(workspace, seal.RequestPath, request, buildSha256, cancellation);
        using var deadline = Deadline(workspace, request, cancellation);
        var token = deadline.Token;
        if (seal.SchemaVersion != "bubo-vfx-preparation-result/v1" || seal.Id != request.Id || seal.Version != request.Version
            || seal.TaskId != (workspace.HostContext?.TaskId ?? "compiled-caller") || seal.BuildSha256 != buildSha256
            || seal.PolicySha256 != Policy(workspace, request) || seal.RequestSha256 != Hash(requestBytes) || seal.PackagePath != "package.json")
            throw new InvalidOperationException("Preparation seal request/task/build/policy identity is stale or transplanted");
        var expectedPaths = new HashSet<string>(StringComparer.Ordinal)
            { "started.json", "request.json", "atlas.png", "package.json" };
        for (var i = 0; i < request.Sources.Count; i++) expectedPaths.Add(Snapshot(i));
        if (seal.Files is null || seal.Files.Count != expectedPaths.Count || seal.Files.Count + 2 > request.Budgets.MaxOutputFiles)
            throw new InvalidOperationException("Preparation inventory is incomplete or exceeds file budget");
        long bytes = manifestBytes.Length;
        foreach (var file in seal.Files)
        {
            if (file is null || !expectedPaths.Remove(file.Path)) throw new InvalidOperationException("Unknown, duplicate or missing preparation inventory file");
            var path = Resolve(workspace, outputPath + "/" + file.Path);
            Match(path, file.Bytes, file.Sha256, request.Budgets.MaxOutputBytes - bytes, token);
            bytes = checked(bytes + file.Bytes);
        }
        if (expectedPaths.Count != 0 || bytes > request.Budgets.MaxOutputBytes)
            throw new InvalidOperationException("Preparation inventory exceeds cumulative bounds");
        CheckInventory(workspace, outputPath, seal.Files.Select(f => f.Path).Append("preparation.json"), token);
        var start = Parse<VfxPreparationStart>(Read(Resolve(workspace, outputPath + "/started.json"), JsonLimit, token));
        var claimBytes = Read(Resolve(workspace, ClaimPath(workspace, request)), JsonLimit, token);
        if (!claimBytes.AsSpan().SequenceEqual(Encode(start))) throw new InvalidOperationException("Preparation permanent version claim differs from owned attempt ancestry");
        bytes = checked(bytes + claimBytes.Length);
        if (bytes > request.Budgets.MaxOutputBytes) throw new InvalidOperationException("Preparation output plus version reservation exceeds byte budget");
        if (start.SchemaVersion != "bubo-vfx-preparation-start/v1" || start.OutputPath != outputPath
            || start.RequestSha256 != seal.RequestSha256 || start.BuildSha256 != buildSha256
            || start.PolicySha256 != seal.PolicySha256 || start.TaskId != seal.TaskId || start.ProcessId <= 0
            || !DateTimeOffset.TryParseExact(start.StartedAt, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
            throw new InvalidOperationException("Preparation start ownership/ancestry differs from its seal");
        for (var i = 0; i < request.Sources.Count; i++)
            Match(Resolve(workspace, outputPath + "/" + Snapshot(i)), request.Sources[i].Bytes,
                request.Sources[i].Sha256, request.Budgets.MaxSourceBytes, token);
        var pixels = Render(workspace, request, outputPath, token);
        VerifyPixels(workspace, request, outputPath, pixels.Image, token);
        var atlas = seal.Files.Single(f => f.Path == "atlas.png");
        var expectedPackage = Package(request, pixels, atlas);
        var actualPackage = Read(Resolve(workspace, outputPath + "/package.json"), VfxJson.MaximumPackageBytes, token);
        if (!expectedPackage.AsSpan().SequenceEqual(actualPackage))
            throw new InvalidOperationException("Prepared package timing/layout/resource metadata differs from independently reproduced inputs");
        if (!VfxValidation.Validate(VfxJson.Parse(actualPackage)).IsValid)
            throw new InvalidOperationException("Prepared package closure is invalid");
        foreach (var file in seal.Files) Match(Resolve(workspace, outputPath + "/" + file.Path), file.Bytes, file.Sha256, request.Budgets.MaxOutputBytes, token);
        CheckInventory(workspace, outputPath, seal.Files.Select(f => f.Path).Append("preparation.json"), token);
        FreshOriginals(workspace, seal.RequestPath, seal.RequestSha256, request, token);
        Match(Resolve(workspace, ClaimPath(workspace, request)), claimBytes.Length, Hash(claimBytes), JsonLimit, token);
        Match(Resolve(workspace, manifestPath), manifestBytes.Length, Hash(manifestBytes), JsonLimit, token);
        return new(manifestPath, Hash(manifestBytes), outputPath + "/package.json", Hash(actualPackage),
            seal.Files, request.Timing.Loop);
    }

    static void Admit(IWorkspace workspace, string requestPath, VfxPreparationRequest request, string build,
        CancellationToken token)
    {
        Canonical(requestPath);
        if (build is null || build.Length != 64 || build.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidOperationException("Preparation requires the exact current lowercase native build SHA-256");
        var validation = PreparationValidation.Validate(request);
        if (!validation.IsValid) throw new InvalidOperationException("Invalid preparation: " + string.Join("; ", validation.Issues.Select(i => i.Path + ": " + i.Message)));
        if (request.Budgets.HardProcessMemoryBytes.HasValue || workspace.HostContext?.MaxPrivateBytes is not null)
            throw new PlatformNotSupportedException("VFX preparation has bounded managed working allocations, but no admitted hard process-memory worker; requested hard ceiling is unavailable");
        if (request.Budgets.MaxWorkingBytes < HostWorkingReservation + RgbaPngCodec.FixedWorkingBytes)
            throw new InvalidOperationException("Preparation allocation budget cannot hold its finite host bookkeeping reservation");
        var context = workspace.HostContext;
        if (context is not null)
        {
            if (context.MediaOnly || context.InspectionOnly) throw new InvalidOperationException("Preparation requires a full art/engine context");
            context.ValidateFresh();
            context.RequireReference(requestPath, "input");
            if (context.StyleSha256 != request.StyleSha256) throw new InvalidOperationException("Preparation STYLE differs from host policy");
            foreach (var (field, expected) in new[] { ("effectiveEngine", request.Engine.Id), ("contractSha256", request.Engine.ContractSha256), ("definitionSha256", request.Engine.DefinitionSha256), ("registrySha256", request.Engine.RegistrySha256) })
                if (context.Engine[field]?.GetValue<string>() != expected) throw new InvalidOperationException("Preparation engine identity differs from host policy");
            foreach (var source in request.Sources) context.RequireReference(source.Path, "input");
        }
        token.ThrowIfCancellationRequested();
    }

    static CancellationTokenSource Deadline(IWorkspace workspace, VfxPreparationRequest request, CancellationToken token)
    {
        var result = CancellationTokenSource.CreateLinkedTokenSource(token);
        result.CancelAfter(TimeSpan.FromSeconds(Math.Min(request.Budgets.MaxSeconds, workspace.HostContext?.TimeoutSeconds ?? request.Budgets.MaxSeconds)));
        return result;
    }
    static string Policy(IWorkspace workspace, VfxPreparationRequest request) => workspace.HostContext is { } context
        ? HostPolicyIdentity.Fingerprint(context) : Hash(Encode(new { request.StyleSha256, request.Engine, scope = "compiled-caller-no-host-approval" }));
    static string Snapshot(int index) => "sources/" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ".png";
    static string ClaimPath(IWorkspace workspace, VfxPreparationRequest request)
    {
        var state = workspace.HostContext?.StateRoot ?? ".bubo/state";
        return state + "/vfx-preparation/" + request.Id + "/" + request.Version + ".json";
    }
    static string Output(IWorkspace workspace, string path)
    {
        var output = Resolve(workspace, path, true);
        var staging = Path.GetFullPath(workspace.HostContext?.StagingRoot ?? ".art-staging", workspace.Root);
        if (!Within(output, staging)) throw new InvalidOperationException("Preparation output must be a child of the declared non-runtime staging root");
        return output;
    }
    static void FreeDisk(string output, long reserve, long proposed)
    {
        var available = new DriveInfo(Path.GetPathRoot(output)!).AvailableFreeSpace;
        if (available < reserve || proposed > available - reserve) throw new InvalidOperationException("Preparation minimum free disk/output reservation is unavailable");
    }
    static VfxPreparedFile Observe(string path, string relative, long maximum, CancellationToken token)
    {
        using var stream = Open(path, maximum);
        var observed = Digest(stream, maximum, token);
        if (stream.Length != observed.Bytes) throw new InvalidOperationException("Preparation file length changed while hashing");
        return new() { Path = relative, Bytes = observed.Bytes, Sha256 = observed.Sha256 };
    }
    static void Match(string path, long bytes, string hash, long maximum, CancellationToken token)
    {
        if (bytes < 1 || bytes > maximum) throw new InvalidOperationException("Preparation file has no positive remaining byte allowance");
        using var stream = Open(path, maximum, bytes);
        var actual = Digest(stream, maximum, token);
        if (actual.Bytes != bytes || actual.Sha256 != hash || stream.Length != bytes)
            throw new InvalidOperationException("Preparation file hash/length changed");
        CheckComponents(path);
    }
    static void FreshOriginals(IWorkspace workspace, string requestPath, string requestHash,
        VfxPreparationRequest request, CancellationToken token)
    {
        var bytes = Read(Resolve(workspace, requestPath), PreparationJson.MaximumRequestBytes, token);
        if (Hash(bytes) != requestHash) throw new InvalidOperationException("Original preparation request changed");
        foreach (var source in request.Sources)
            Match(Resolve(workspace, source.Path), source.Bytes, source.Sha256, request.Budgets.MaxSourceBytes, token);
        workspace.HostContext?.ValidateFresh();
        token.ThrowIfCancellationRequested();
    }
    static void CheckInventory(IWorkspace workspace, string outputPath, IEnumerable<string> files, CancellationToken token)
    {
        var expected = new HashSet<string>(files, StringComparer.Ordinal);
        var directories = new Stack<string>();
        directories.Push(outputPath);
        while (directories.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var relative = directories.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(Resolve(workspace, relative)))
            {
                token.ThrowIfCancellationRequested(); CheckComponents(entry);
                var name = Path.GetRelativePath(Resolve(workspace, outputPath), entry).Replace('\\', '/');
                if (Directory.Exists(entry))
                {
                    if (name != "sources" || !expected.Any(e => e.StartsWith("sources/", StringComparison.Ordinal)))
                        throw new InvalidOperationException("Unowned preparation directory in sealed closure");
                    directories.Push(outputPath + "/" + name);
                }
                else if (!expected.Remove(name)) throw new InvalidOperationException("Unowned/aliased preparation file in sealed closure");
            }
        }
        if (expected.Count != 0) throw new InvalidOperationException("Missing preparation inventory file");
    }

    // Pixel port and typed package construction are integrated with their disjoint authoring lanes.
    static VfxPreparedPixels Render(IWorkspace workspace, VfxPreparationRequest request, string outputPath, CancellationToken token)
        => VfxPixelPreparation.Render(request with { Budgets = request.Budgets with { MaxWorkingBytes = request.Budgets.MaxWorkingBytes - HostWorkingReservation } }, (source, limits, cancellation) =>
        {
            var index = request.Sources.ToList().FindIndex(s => s == source);
            if (index < 0) throw new InvalidOperationException("Unknown preparation source port request");
            var path = Resolve(workspace, outputPath + "/" + Snapshot(index));
            using var stream = Open(path, request.Budgets.MaxSourceBytes, source.Bytes);
            return RgbaPngCodec.Read(stream, limits, cancellation);
        }, token);
    static void VerifyPixels(IWorkspace workspace, VfxPreparationRequest request, string outputPath,
        RgbaPngCodec.Image expected, CancellationToken token)
    {
        using var stream = Open(Resolve(workspace, outputPath + "/atlas.png"), request.Budgets.MaxOutputBytes);
        var actual = RgbaPngCodec.Read(stream, OutputLimits(request) with
        {
            MaxWorkingBytes = request.Budgets.MaxWorkingBytes - HostWorkingReservation - expected.Pixels.LongLength
        }, token);
        if (actual.Width != expected.Width || actual.Height != expected.Height || !actual.Pixels.AsSpan().SequenceEqual(expected.Pixels))
            throw new InvalidOperationException("Independent atlas reopening differs from exact prepared pixels");
    }
    static RgbaPngCodec.Limits OutputLimits(VfxPreparationRequest request) => new()
    {
        MaxEncodedBytes = Math.Min(134217728, request.Budgets.MaxOutputBytes),
        MaxChunkBytes = 134217728, MaxCompressedBytes = 134217728,
        MaxPixels = request.Budgets.MaxAtlasPixels,
        MaxWorkingBytes = request.Budgets.MaxWorkingBytes - HostWorkingReservation,
        MaxOutputBytes = Math.Min(134217728, request.Budgets.MaxOutputBytes),
        ColorSpace = request.Atlas.ColorSpace
    };
    static byte[] Package(VfxPreparationRequest request, VfxPreparedPixels pixels, VfxPreparedFile atlas)
    {
        var duration = (double)pixels.Atlas.DurationNumerator / pixels.Atlas.DurationDenominator;
        return VfxJson.Serialize(new VfxPackage
        {
            SchemaVersion = "bubo-vfx-package/v2", Id = request.Id, Version = request.Version,
            StyleSha256 = request.StyleSha256, Engine = request.Engine,
            Budgets = new()
            {
                MaxPackageBytes = VfxJson.MaximumPackageBytes,
                MaxDependencyBytes = atlas.Bytes, MaxTotalDependencyBytes = atlas.Bytes,
                MaxDependencies = 1, MaxEffects = 1, MaxLayers = 1, MaxParticles = 1,
                MaxVertices = 4, MaxDrawCalls = 1, MaxTrailPoints = 1, MaxDurationSeconds = duration
            },
            Resources = [new() { Id = "atlas", Kind = "texture-png", Path = "atlas.png", Bytes = atlas.Bytes, Sha256 = atlas.Sha256 }],
            Effects = [new()
            {
                Id = request.Id, DurationSeconds = duration,
                Layers = [new() { Id = "sprite", Kind = "sprite", StartSeconds = 0, DurationSeconds = duration,
                    ResourceIds = ["atlas"], MaxParticles = 1, MaxVertices = 4, MaxDrawCalls = 1, MaxTrailPoints = 0 }]
            }],
            Atlases = [pixels.Atlas]
        });
    }
}
