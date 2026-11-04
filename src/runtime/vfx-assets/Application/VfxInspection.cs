using Hephaera.AssetProduction;
using Bubo.Vfx.Domain;

namespace Bubo.Vfx.Application;

/// <summary>
/// Bounded, read-only inspection of an exact declarative package and its resource closure.
/// Filesystem guards are cooperative snapshots, not an operating-system sandbox or decoder.
/// </summary>
public static class VfxInspection
{
    const int BlockBytes = 64 * 1024;
    static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static VfxInspectionResult Inspect(IWorkspace workspace, string packagePath,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        cancellation.ThrowIfCancellationRequested();
        try { return InspectCore(workspace, packagePath, cancellation); }
        catch (IOException error) { throw new InvalidOperationException("Cannot inspect VFX input safely: " + error.Message, error); }
        catch (UnauthorizedAccessException error) { throw new InvalidOperationException("VFX input is not readable", error); }
    }

    static VfxInspectionResult InspectCore(IWorkspace workspace, string packagePath, CancellationToken cancellation)
    {
        CheckPathSyntax(packagePath, false);
        var fullPackage = Resolve(workspace, packagePath, cancellation);
        if (!Path.GetExtension(fullPackage).Equals(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A VFX package must be a JSON file");
        var packageData = Read(workspace, fullPackage, VfxJson.MaximumPackageBytes, null, true, cancellation);
        var package = VfxJson.Parse(packageData.Content!);
        var validation = VfxValidation.Validate(package);
        var observed = new List<VfxObservedResource>();
        var requirements = new VfxRequirements(package.Engine, package.StyleSha256,
            Array.AsReadOnly(package.Effects.SelectMany(effect => effect.Layers).Select(layer => layer.Kind)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(package.Resources.Select(resource => resource.Kind)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
        var result = new VfxInspectionResult(package, ProjectRelative(workspace, fullPackage), packageData.Sha256,
            packageData.Bytes, observed.AsReadOnly(), validation, requirements,
            new VfxCapabilities { ResourceContentValidationAvailable = package.SchemaVersion == "bubo-vfx-package/v2" });
        // Semantic errors are reported without opening paths from an invalid declaration.
        if (!validation.IsValid) return result;
        if (packageData.Bytes > package.Budgets.MaxPackageBytes)
            throw new InvalidOperationException("VFX package exceeds its declared package byte budget");

        var directory = Path.GetDirectoryName(fullPackage)!;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fullPackage };
        long totalBytes = 0;
        foreach (var resource in package.Resources)
        {
            cancellation.ThrowIfCancellationRequested();
            CheckPathSyntax(resource.Path, true);
            var fullResource = Path.GetFullPath(resource.Path.Replace('/', Path.DirectorySeparatorChar), directory);
            if (!Within(fullResource, directory) || fullResource.Equals(directory, PathComparison))
                throw new InvalidOperationException("VFX resource escapes its package directory");
            var resolved = Resolve(workspace, fullResource, cancellation);
            if (!resolved.Equals(fullResource, PathComparison) || !paths.Add(resolved))
                throw new InvalidOperationException("VFX resource aliases the package or another dependency");
            var expectedExtension = resource.Kind switch
            {
                "texture-png" => ".png",
                "mesh-glb" => ".glb",
                _ => throw new InvalidOperationException("Unsupported VFX dependency kind")
            };
            if (!Path.GetExtension(resolved).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("VFX dependency kind and file extension differ");
            var available = package.Budgets.MaxTotalDependencyBytes - totalBytes;
            var limit = Math.Min(package.Budgets.MaxDependencyBytes, available);
            if (resource.Bytes < 1 || resource.Bytes > limit)
                throw new InvalidOperationException("VFX dependency exceeds the remaining declared byte budget");
            var atlas = package.Atlases?.SingleOrDefault(a => a.ResourceId == resource.Id);
            var data = Read(workspace, resolved, limit, resource.Bytes, false, cancellation, resource.Kind, atlas);
            if (!data.Sha256.Equals(resource.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException("VFX dependency hash changed: " + resource.Id);
            totalBytes = checked(totalBytes + data.Bytes);
            observed.Add(new(resource.Id, resource.Kind, resource.Path, ProjectRelative(workspace, resolved), data.Bytes, data.Sha256));
        }

        // A later dependency/path callback must not silently replace the declaration being reported.
        var packageAgain = Read(workspace, fullPackage, VfxJson.MaximumPackageBytes, packageData.Bytes, false, cancellation);
        if (packageAgain.Sha256 != packageData.Sha256)
            throw new InvalidOperationException("VFX package changed during dependency inspection");
        cancellation.ThrowIfCancellationRequested();
        return result;
    }

    sealed record ReadResult(long Bytes, string Sha256, byte[]? Content);

    static ReadResult Read(IWorkspace workspace, string path, long maximumBytes, long? expectedBytes,
        bool retainContent, CancellationToken cancellation, string? resourceKind = null, VfxAtlasMetadata? atlas = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (maximumBytes < 1) throw new InvalidOperationException("VFX input has no remaining byte allowance");
        var resolved = Resolve(workspace, path, cancellation);
        // Unix FIFOs/devices can satisfy File.Exists but block while opening.
        // Reject entries without a finite positive declared snapshot first;
        // the opened stream is still independently checked and hashed below.
        var snapshotLength = new FileInfo(resolved).Length;
        if (snapshotLength < 1 || snapshotLength > maximumBytes
            || (expectedBytes is not null && snapshotLength != expectedBytes))
            throw new InvalidOperationException("VFX input has no matching finite bounded file length");
        cancellation.ThrowIfCancellationRequested();
        using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read,
            BlockBytes, FileOptions.SequentialScan);
        using var reader = new VfxInputReader(stream, maximumBytes, expectedBytes, retainContent, cancellation);
        if (resourceKind is null) reader.Drain();
        else VfxResourceEnvelope.Validate(reader, resourceKind, cancellation);
        reader.Complete();
        var digest = reader.FinishHash();
        if (atlas is not null)
        {
            // Explicit v2 atlas admission decodes the already hashed, still-open file.
            // v1 retains its original envelope-only profile. No colour conversion occurs.
            stream.Position = 0;
            var decoded = RgbaPngCodec.Read(stream, new RgbaPngCodec.Limits
            {
                MaxEncodedBytes = Math.Min(maximumBytes, 134217728), MaxChunkBytes = 134217728,
                MaxCompressedBytes = 134217728, MaxPixels = checked((long)atlas.Width * atlas.Height),
                MaxWorkingBytes = 268435456, MaxOutputBytes = 134217728, ColorSpace = atlas.ColorSpace
            }, cancellation);
            if (decoded.Width != atlas.Width || decoded.Height != atlas.Height)
                throw new InvalidOperationException("VFX atlas dimensions differ from actual decoded PNG pixels");
            stream.Position = 0;
            using var afterDecode = new VfxInputReader(stream, maximumBytes, expectedBytes, false, cancellation);
            afterDecode.Drain(); afterDecode.Complete();
            if (afterDecode.FinishHash() != digest)
                throw new InvalidOperationException("VFX atlas changed while its content was decoded");
        }
        var checkedAgain = Resolve(workspace, path, cancellation);
        if (!checkedAgain.Equals(resolved, PathComparison))
            throw new InvalidOperationException("VFX input path changed during bounded inspection");
        return new(reader.Bytes, digest, reader.Content);
    }

    static string Resolve(IWorkspace workspace, string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Root));
        var full = Path.GetFullPath(workspace.Resolve(path));
        var expected = Path.GetFullPath(path, root);
        if (!full.Equals(expected, PathComparison))
            throw new InvalidOperationException("VFX workspace resolution changed the requested input identity");
        if (!Within(full, root) || full.Equals(root, PathComparison))
            throw new InvalidOperationException("VFX input escapes or aliases the project root");
        CheckComponents(full, cancellation);
        if (!File.Exists(full)) throw new InvalidOperationException("VFX input is missing or is not a regular file");
        return full;
    }

    static void CheckComponents(string full, CancellationToken cancellation)
    {
        var volume = Path.GetPathRoot(full) ?? throw new InvalidOperationException("VFX input has no filesystem root");
        var current = volume;
        Check(current);
        foreach (var part in Path.GetRelativePath(volume, full).Split(Path.DirectorySeparatorChar))
        {
            cancellation.ThrowIfCancellationRequested();
            var matches = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(current, part, new EnumerationOptions
            {
                MatchCasing = MatchCasing.CaseInsensitive, MatchType = MatchType.Simple,
                RecurseSubdirectories = false, ReturnSpecialDirectories = false, AttributesToSkip = 0
            }))
            {
                cancellation.ThrowIfCancellationRequested();
                if (++matches > 1 || !Path.GetFileName(entry).Equals(part, StringComparison.Ordinal))
                    throw new InvalidOperationException("VFX path contains a filesystem case alias or collision");
            }
            if (matches != 1) throw new InvalidOperationException("VFX path component is missing");
            current = Path.Combine(current, part);
            Check(current);
        }
        void Check(string component)
        {
            cancellation.ThrowIfCancellationRequested();
            if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("VFX paths cannot contain symbolic links or reparse points");
        }
    }

    static string ProjectRelative(IWorkspace workspace, string full)
        => Path.GetRelativePath(Path.GetFullPath(workspace.Root), full).Replace('\\', '/');

    static bool Within(string candidate, string directory)
        => candidate.Equals(directory, PathComparison)
            || candidate.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, PathComparison);

    static void CheckPathSyntax(string path, bool dependency)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Any(char.IsControl))
            throw new InvalidOperationException("VFX paths must be bounded nonempty single-line strings");
        if (dependency && (path.Contains('\\') || Path.IsPathRooted(path) || path.StartsWith('/')))
            throw new InvalidOperationException("VFX dependency paths must be canonical package-relative forward-slash paths");
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException("VFX device and network paths are unsupported");
        var parts = normalized.Split('/');
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (!dependency && index == 0 && (part.Length == 0 || (OperatingSystem.IsWindows()
                && part.Length == 2 && char.IsAsciiLetter(part[0]) && part[1] == ':'))) continue;
            if (part.Length == 0 || part is "." or ".." || part.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0)
                throw new InvalidOperationException("VFX path contains traversal, reserved names or noncanonical aliases");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && stem[3] is >= '1' and <= '9'
                && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))))
                throw new InvalidOperationException("VFX device-name aliases are unsupported");
        }
    }
}
