using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bubo.FloorPlan.Domain;
using Hephaera.AssetProduction;

namespace Bubo.FloorPlan.Infrastructure;

internal sealed record CurrentExportPayload(FloorPlanExportPackage Package, FloorPlanSemantics Semantics,
    string Directory, string Manifest, string Sha256);

/// <summary>Exclusive portable payload creation and closed-inventory validation; no asset promotion.</summary>
internal sealed class ExportPayload(IWorkspace workspace, string expectedCatalogSha256)
{
    internal CurrentExportPayload Seal(ConstructionSession.CurrentConstruction source, ExportAttempt attempt,
        ExportNativeReport producer, string catalogSha256, CancellationToken cancellation)
    {
        if (catalogSha256 != expectedCatalogSha256) throw new InvalidOperationException("Payload catalog differs from the compiled resource identity");
        var directory = attempt.Directory + "/package";
        var absolute = workspace.ResolveWrite(directory);
        if (Directory.Exists(absolute) || File.Exists(absolute)) throw new InvalidOperationException("Export payload already exists; preserve immutable attempts");
        Directory.CreateDirectory(absolute);
        var maximum = Math.Min(source.Design.Plan.Budgets.MaxExportBytes, ExportPackageValidator.MaxPayloadBytes);
        long copied = 0;
        var files = new List<ExportFileEntry>();
        void Copy(string original, string target, string expected)
        {
            var destination = directory + "/" + target;
            var entry = CopyExact(original, destination, expected, maximum - copied, cancellation);
            copied = checked(copied + entry.Bytes); files.Add(entry with { Path = target });
        }
        var preview = source.Design.Manifest.Previews.Last();
        var approval = source.Design.Manifest.Reviews.Last();
        Copy(source.BlendPath, "building.blend", source.Attempt.BlendSha256!);
        Copy(source.Design.Manifest.OriginalPlan, "building-plan.json", source.Attempt.PlanSha256);
        Copy(source.ReportPath, "provenance/construction-report.json", source.Attempt.ReportSha256!);
        Copy(preview.Directory + "/preview-index.json", "provenance/design-preview.json", preview.IndexSha256!);
        Copy(approval.Evidence, "provenance/design-approval.json", source.Attempt.ApprovalSha256);
        foreach (var (name, hash) in source.Design.Package!.Drawings)
            Copy(preview.Directory + "/" + name, "drawings/" + name, hash);
        foreach (var representation in new[] { "visual", "collision" })
        {
            var output = producer.Outputs.Single(f => f.Path == representation + ".glb");
            Copy(attempt.Directory + "/produce/output/" + output.Path, output.Path, output.Sha256);
        }
        var semantics = ExportPackageValidator.CreateSemantics(source.Design.Plan, source.Job.Scene,
            source.Attempt.PlanSha256, source.Attempt.JobSha256!, source.Attempt.BlendSha256!, cancellation);
        var semanticPath = directory + "/semantics.json";
        WriteJson(semanticPath, semantics, Math.Min(ExportPackageValidator.MaxSemanticBytes, maximum - copied), cancellation);
        var semanticFile = Describe(semanticPath, Math.Min(ExportPackageValidator.MaxSemanticBytes, maximum - copied), cancellation);
        copied = checked(copied + semanticFile.Bytes); files.Add(new("semantics.json", semanticFile.Sha256, semanticFile.Bytes));
        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        var version = Regex.Match(producer.BlenderVersion, "^4\\.5\\.[0-9]+", RegexOptions.CultureInvariant).Value;
        if (version.Length == 0) throw new InvalidOperationException("Producer has no supported measured Blender version");
        var package = new FloorPlanExportPackage
        {
            SchemaVersion = ExportPackageValidator.PackageSchema, Task = source.Design.Manifest.Task,
            Asset = source.Design.Manifest.Asset, Version = source.Design.Manifest.Version,
            Coordinates = ExportPackageValidator.Coordinates(), Numerics = source.Design.Plan.Numerics,
            Ancestry = new()
            {
                ModelAuthor = attempt.ModelAuthor, PlanSha256 = source.Attempt.PlanSha256,
                DesignFingerprint = source.Attempt.DesignFingerprint, ApprovalSha256 = source.Attempt.ApprovalSha256,
                RuntimeSha256 = source.Job.Identities.RuntimeSha256, CatalogSha256 = catalogSha256,
                ConstructionAttempt = source.Attempt.Directory, ConstructionJobSha256 = source.Attempt.JobSha256!,
                ConstructionReportSha256 = source.Attempt.ReportSha256!, SourceSha256 = source.Attempt.BlendSha256!,
                Bundle = new(attempt.Bundle, StringComparer.Ordinal),
                Toolchain = new()
                {
                    BlenderSha256 = attempt.Toolchain.BlenderSha256, BlenderVersion = version,
                    GltfFiles = attempt.Toolchain.Files.Select(f => new ExportFileEntry(f.Path, f.Sha256, f.Bytes)).ToList()
                }
            }, Files = files
        };
        ExportPackageValidator.RequireValidPackage(package, DrawingPaths(source), maximum);
        ExportPackageValidator.RequireMatchingSemantics(package, semantics);
        var manifest = directory + "/package.json";
        WriteJson(manifest, package, Math.Min(StrictPlanJson.MaximumInputBytes, maximum - copied), cancellation);
        var manifestSha256 = ConstructionOutputVerifier.FileHash(workspace, manifest, cancellation, StrictPlanJson.MaximumInputBytes);
        return Inspect(source, attempt, manifestSha256, cancellation);
    }

    internal CurrentExportPayload Inspect(ConstructionSession.CurrentConstruction source, ExportAttempt attempt,
        string expectedSha256, CancellationToken cancellation)
    {
        var directory = attempt.Directory + "/package"; var manifest = directory + "/package.json";
        using var manifestRead = new FileStream(workspace.Resolve(manifest), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (ConstructionOutputVerifier.FileHash(workspace, manifest, cancellation, StrictPlanJson.MaximumInputBytes) != expectedSha256)
            throw new InvalidOperationException("Sealed export package changed");
        var package = ExportJson.Read<FloorPlanExportPackage>(workspace.Resolve(manifest), StrictPlanJson.MaximumInputBytes, cancellation);
        var maximum = Math.Min(source.Design.Plan.Budgets.MaxExportBytes, ExportPackageValidator.MaxPayloadBytes);
        ExportPackageValidator.RequireValidPackage(package, DrawingPaths(source), maximum);
        var ancestry = package.Ancestry;
        if (package.Task != source.Design.Manifest.Task || package.Asset != source.Design.Manifest.Asset || package.Version != source.Design.Manifest.Version
            || ancestry.ModelAuthor != attempt.ModelAuthor || ancestry.PlanSha256 != source.Attempt.PlanSha256
            || ancestry.DesignFingerprint != source.Attempt.DesignFingerprint || ancestry.ApprovalSha256 != source.Attempt.ApprovalSha256
            || ancestry.RuntimeSha256 != source.Job.Identities.RuntimeSha256 || ancestry.CatalogSha256 != expectedCatalogSha256
            || ancestry.ConstructionAttempt != source.Attempt.Directory
            || ancestry.ConstructionJobSha256 != source.Attempt.JobSha256 || ancestry.ConstructionReportSha256 != source.Attempt.ReportSha256
            || ancestry.SourceSha256 != source.Attempt.BlendSha256 || ancestry.Toolchain.BlenderSha256 != attempt.Toolchain.BlenderSha256
            || ancestry.Bundle.Count != attempt.Bundle.Count || attempt.Bundle.Any(p => ancestry.Bundle.GetValueOrDefault(p.Key) != p.Value)
            || !ancestry.Toolchain.GltfFiles.SequenceEqual(attempt.Toolchain.Files.Select(f => new ExportFileEntry(f.Path, f.Sha256, f.Bytes))))
            throw new InvalidOperationException("Export package belongs to a different construction, author or toolchain");
        var expected = package.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var actual = Inventory(directory, maximum, cancellation);
        if (!actual.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected.Keys.Append("package.json")))
            throw new InvalidOperationException("Sealed package contains unexpected or missing files");
        using var leases = new ReadLeases();
        foreach (var file in package.Files) leases.Add(workspace.Resolve(directory + "/" + file.Path));
        foreach (var file in package.Files)
        {
            cancellation.ThrowIfCancellationRequested();
            if (actual[file.Path] != file.Bytes || ConstructionOutputVerifier.FileHash(workspace, directory + "/" + file.Path, cancellation, file.Bytes) != file.Sha256)
                throw new InvalidOperationException("Sealed payload file changed: " + file.Path);
        }
        var semantics = ExportJson.Read<FloorPlanSemantics>(workspace.Resolve(directory + "/semantics.json"), Math.Min(ExportPackageValidator.MaxSemanticBytes, maximum), cancellation);
        ExportPackageValidator.RequireMatchingSemantics(package, semantics);
        ExportPackageValidator.RequireValidSemantics(semantics, source.Design.Plan, source.Job.Scene, source.Attempt.PlanSha256,
            source.Attempt.JobSha256!, source.Attempt.BlendSha256!, cancellation);
        if (ConstructionOutputVerifier.FileHash(workspace, manifest, cancellation, StrictPlanJson.MaximumInputBytes) != expectedSha256)
            throw new InvalidOperationException("Export package changed during inspection");
        var final = Inventory(directory, maximum, cancellation);
        if (!final.SequenceEqual(actual)) throw new InvalidOperationException("Payload inventory changed during inspection");
        cancellation.ThrowIfCancellationRequested();
        return new(package, semantics, directory, manifest, expectedSha256);
    }

    internal ExportNativeFile Describe(string path, long maximum, CancellationToken cancellation)
    {
        var absolute = workspace.Resolve(path); var length = new FileInfo(absolute).Length;
        ExportFileGuards.NoAlternateStreams(absolute, cancellation);
        var hash = ConstructionOutputVerifier.FileHash(workspace, path, cancellation, maximum);
        if (new FileInfo(workspace.Resolve(path)).Length != length) throw new InvalidOperationException("Export reference changed during inspection");
        return new(absolute, hash, length);
    }

    internal ExportFileEntry CopyExact(string source, string destination, string expected, long maximum, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var input = workspace.Resolve(source); var output = workspace.ResolveWrite(destination);
        ExportFileGuards.NoAlternateStreams(input, cancellation);
        if (input.Equals(output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Export cannot overwrite a source file");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var reader = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (reader.Length <= 0 || reader.Length > maximum) throw new InvalidOperationException("Export copy exceeds remaining byte allowance");
        var expectedLength = reader.Length;
        using var writer = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65_536]; long total = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested(); var read = reader.Read(buffer); if (read == 0) break;
            total = checked(total + read); if (total > maximum) throw new InvalidOperationException("Export copy grew beyond allowance");
            digest.AppendData(buffer, 0, read); writer.Write(buffer, 0, read);
        }
        if (total != expectedLength || Convert.ToHexStringLower(digest.GetHashAndReset()) != expected)
            throw new InvalidOperationException("Export source changed during copying");
        writer.Flush(true); cancellation.ThrowIfCancellationRequested();
        return new(destination, expected, total);
    }

    internal void WriteJson<T>(string path, T value, long maximum, CancellationToken cancellation)
    {
        if (maximum < 2 || maximum > ExportJson.HardMaximumBytes) throw new InvalidOperationException("Invalid export JSON output allowance");
        var absolute = workspace.ResolveWrite(path); Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        using var file = new FileStream(absolute, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var bounded = new LimitedStream(file, maximum, cancellation);
        JsonSerializer.Serialize(bounded, value, StrictPlanJson.Options); file.Flush(true);
        cancellation.ThrowIfCancellationRequested();
    }

    internal SortedDictionary<string, long> Inventory(string directory, long maximum, CancellationToken cancellation)
    {
        var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
        ExportFileGuards.NoAlternateStreams(workspace.Resolve(directory), cancellation);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(); queue.Enqueue(workspace.Resolve(directory));
        var count = 0; long total = 0;
        while (queue.TryDequeue(out var parent))
        foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
        {
            cancellation.ThrowIfCancellationRequested();
            if (++count > ExportPackageValidator.MaxInventoryFiles * 2) throw new InvalidOperationException("Export inventory entry ceiling exceeded");
            var relative = Path.GetRelativePath(workspace.Resolve(directory), workspace.Resolve(entry)).Replace('\\', '/');
            ExportFileGuards.NoAlternateStreams(entry, cancellation);
            if (!aliases.Add(relative) || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked or ambiguous export inventory entry");
            if (Directory.Exists(entry))
            {
                if (relative is not ("drawings" or "provenance")) throw new InvalidOperationException("Unexpected sealed payload directory");
                queue.Enqueue(entry); continue;
            }
            var length = new FileInfo(entry).Length;
            if (length < 0 || length > maximum - total) throw new InvalidOperationException("Export payload exceeds cumulative byte allowance");
            total = checked(total + length); result.Add(relative, length);
        }
        cancellation.ThrowIfCancellationRequested(); return result;
    }

    internal static string[] DrawingPaths(ConstructionSession.CurrentConstruction source)
        => source.Design.Package!.Drawings.Keys.Select(name => "drawings/" + name).ToArray();

    sealed class ReadLeases : IDisposable
    {
        readonly List<FileStream> files = [];
        public void Add(string path) => files.Add(new(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        public void Dispose() { foreach (var file in files) file.Dispose(); }
    }

    sealed class LimitedStream(Stream inner, long maximum, CancellationToken cancellation) : Stream
    {
        long written;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => written; public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            cancellation.ThrowIfCancellationRequested();
            if (buffer.Length > maximum - written) throw new InvalidOperationException("Export JSON exceeds remaining allowance");
            inner.Write(buffer); written = checked(written + buffer.Length);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
