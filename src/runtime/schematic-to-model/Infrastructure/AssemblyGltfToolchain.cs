using System.Security.Cryptography;
using System.Text;
using Bubo.Schematic.Domain;
using System.Globalization;

namespace Bubo.Schematic.Infrastructure;


/// <summary>Read-only identity of the bundled glTF code associated with the explicitly selected Blender installation.</summary>
public static class AssemblyGltfToolchain
{
    const int MaximumFiles = 4096, MaximumDirectories = 1024;
    const long MaximumFileBytes = 16_777_216, MaximumTotalBytes = 134_217_728;

    public static AssemblyExportToolchain Capture(string blender, string expectedExecutableSha256, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!System.IO.Path.IsPathFullyQualified(blender) || blender.Any(char.IsControl)) throw new InvalidOperationException("Explicit absolute Blender tool identity required");
        blender = System.IO.Path.GetFullPath(blender);
        var installation = System.IO.Path.GetDirectoryName(blender)!;
        var root = System.IO.Path.Combine(installation, "4.5", "scripts", "addons_core", "io_scene_gltf2");
        if (!Directory.Exists(root)) throw new InvalidOperationException("Selected Blender has no supported bundled glTF module; no installation or fallback");
        RequireNoLinks(root, installation);
        var actualExecutable = HashFile(blender, 1_073_741_824, cancellation);
        if (actualExecutable.Sha256 != expectedExecutableSha256) throw new InvalidOperationException("Blender executable changed before glTF toolchain inspection");
        var directories = new Queue<string>(); directories.Enqueue(root);
        var directoryCount = 1; long total = 0;
        var files = new List<AssemblyExportToolchainFile>();
        var names = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        while (directories.TryDequeue(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellation.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked glTF toolchain entries are unsupported");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (++directoryCount > MaximumDirectories) throw new InvalidOperationException("glTF module directory ceiling exceeded");
                    directories.Enqueue(path); continue;
                }
                if (files.Count >= MaximumFiles) throw new InvalidOperationException("glTF module file ceiling exceeded");
                var relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.StartsWith("../", StringComparison.Ordinal) || relative.Any(char.IsControl) || !names.Add(relative))
                    throw new InvalidOperationException("Ambiguous glTF module inventory path");
                var file = HashFile(path, Math.Min(MaximumFileBytes, MaximumTotalBytes - total), cancellation);
                total = checked(total + file.Bytes);
                files.Add(new(relative, file.Sha256, file.Bytes));
            }
        }
        if (files.Count == 0 || !names.Contains("__init__.py")) throw new InvalidOperationException("Bundled glTF module is incomplete");
        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        RequireNoLinks(root, installation);
        // Cross-language canonical text; no platform newline or JSON escaping dependence.
        // File order is .NET ordinal (UTF-16 code units); all separators are prohibited in paths.
        var content = string.Join('\n', new[] { blender, expectedExecutableSha256, root }
            .Concat(files.Select(file => file.Path + "\t" + file.Sha256 + "\t" + file.Bytes.ToString(CultureInfo.InvariantCulture)))) + "\n";
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(content)));
        cancellation.ThrowIfCancellationRequested();
        return new(blender, expectedExecutableSha256, root, files, fingerprint);
    }

    public static void RequireCurrent(AssemblyExportToolchain expected, CancellationToken cancellation)
    {
        var current = Capture(expected.BlenderPath, expected.BlenderSha256, cancellation);
        if (current.Fingerprint != expected.Fingerprint || current.BlenderPath != expected.BlenderPath
            || current.ModuleRoot != expected.ModuleRoot || current.BlenderSha256 != expected.BlenderSha256
            || !current.Files.SequenceEqual(expected.Files))
            throw new InvalidOperationException("Bundled glTF code or recorded inventory changed; preserve the attempt and refresh explicit toolchain evidence");
    }

    static void RequireNoLinks(string path, string installation)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        while (true)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked Blender/glTF installation path is unsupported");
            if (path.Equals(installation, comparison)) return;
            path = System.IO.Path.GetDirectoryName(path) ?? throw new InvalidOperationException("glTF module escaped selected installation");
        }
    }

    static (string Sha256, long Bytes) HashFile(string path, long maximum, CancellationToken cancellation)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length < 0 || info.Length > maximum)
            throw new InvalidOperationException("Missing, linked or oversized glTF toolchain file");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != info.Length) throw new InvalidOperationException("glTF toolchain file changed size");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65_536]; long count = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = stream.Read(buffer); if (read == 0) break;
            count = checked(count + read); if (count > maximum) throw new InvalidOperationException("glTF toolchain grew beyond its byte ceiling");
            hash.AppendData(buffer, 0, read);
        }
        if (count != info.Length) throw new InvalidOperationException("glTF toolchain changed during hashing");
        cancellation.ThrowIfCancellationRequested();
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), count);
    }
}
