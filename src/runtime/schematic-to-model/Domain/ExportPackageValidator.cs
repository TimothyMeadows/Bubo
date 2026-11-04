using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Bubo.Schematic.Domain;

/// <summary>Pure validation of portable inventory and physical occurrence ownership; performs no file access.</summary>
public static class ExportPackageValidator
{
    public const int MaximumFiles = 16_384;
    public const long MaximumBytes = 17_179_869_184;
    static void Need([DoesNotReturnIf(false)] bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Schematic export package: " + message); }
    public static bool IsHash(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    public static bool IsPortablePath(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 1024
        && !path.Any(c => char.IsControl(c) || "\\:*?<>|\"".Contains(c)) && !path.StartsWith('/')
        && path.Split('/').All(s => s.Length is > 0 and <= 255 && s is not "." and not ".." && !s.EndsWith('.') && !s.EndsWith(' ')
            && !Regex.IsMatch(s.Split('.')[0], "^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    public static void RequireIdentity(AssemblyExportIdentity identity)
    {
        var build = identity.Build;
        Need(new[] { build.DesignSha256, build.DesignFingerprint, build.RuntimeBuildSha256, build.ToolSha256,
            build.PartsJobSha256, build.PartsReportSha256, build.PartsVerificationSha256, identity.ApprovalSha256,
            identity.AssemblyJobSha256, identity.AssemblyReportSha256, identity.AssemblyVerificationSha256,
            identity.AssemblySourceSha256, identity.MotionJobSha256, identity.MotionReportSha256,
            identity.MotionVerificationSha256, identity.MotionSourceSha256 }.All(IsHash), "invalid ancestry hash");
    }
    public static void RequireValid(AssemblyExportPackage package, AssemblyDesign design)
    {
        Need(package.SchemaVersion == "bubo-schematic-export-package/v1", "unsupported schema");
        RequireIdentity(package.Ancestry);
        foreach (var value in new[] { package.Task, package.Asset, package.Version, package.ModelAuthor })
            Need(!string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl), "missing/bounded identity");
        Need(package.Numerics == design.Numerics, "approved numerical policy differs");
        Need(package.Coordinates.Source == "right-handed-metres-z-up" && package.Coordinates.Target == "gltf-right-handed-metres-y-up"
            && package.Coordinates.SourceToGltf.SequenceEqual(new double[] { 1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1 }), "coordinate conversion differs");
        Need(package.Components.Count == design.Instances.Count, "physical occurrence inventory differs");
        var nodes = new HashSet<string>(StringComparer.Ordinal); var rigs = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < design.Instances.Count; i++)
        {
            var expected = design.Instances[i]; var part = design.Parts.Single(p => p.Id == expected.PartId); var component = package.Components[i];
            var flexible = part.Kind != PartKind.Rigid;
            Need(component.InstanceId == expected.Id && component.PartId == expected.PartId && component.Node == "bubo.instance." + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)
                && component.Kind == (flexible ? "flexible" : "rigid") && nodes.Add(component.Node), "component identity/independent node differs");
            Need(flexible ? component.RigNode == "bubo.rig." + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) && rigs.Add(component.RigNode)
                : component.RigNode is null, "owned rig differs");
        }
        Need(package.Files.Count is > 0 and <= MaximumFiles, "bounded payload inventory required");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var file in package.Files)
        {
            Need(IsPortablePath(file.Path) && file.Path != "package.json" && names.Add(file.Path), "ambiguous/conflicting payload path");
            Need(IsHash(file.Sha256) && file.Bytes > 0 && file.Bytes <= Math.Min(MaximumBytes, design.Budgets.MaxExportBytes) - total, "invalid/budget-exceeding payload file");
            total = checked(total + file.Bytes);
        }
        var exactNames = package.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        Need(new[] { "assembly.blend", "assembly.glb", "assembly-design.json", "assembly-metadata.json", "bom.json",
            "diagnostic-motion/diagnostic.blend", "diagnostic-motion/diagnostic.glb", "diagnostic-motion/diagnostic-motion.json" }.All(exactNames.Contains), "canonical payload files are missing");
        Need(names.Any(p => p.StartsWith("drawings/", StringComparison.Ordinal)) && names.Any(p => p.StartsWith("provenance/", StringComparison.Ordinal)), "approved drawings/provenance missing");
        Need(package.Bundle.Count is > 0 and <= 64 && package.Bundle.All(p => IsPortablePath(p.Key) && IsHash(p.Value)), "audited bundle inventory invalid");
        Need(IsHash(package.Toolchain.BlenderSha256) && package.Toolchain.BlenderSha256 == package.Ancestry.Build.ToolSha256
            && Regex.IsMatch(package.Toolchain.BlenderVersion, "^4\\.5\\.[0-9]+(?: LTS)?$", RegexOptions.CultureInvariant), "tool identity differs");
        Need(package.Toolchain.GltfFiles.Count is > 0 and <= 4096, "complete bounded glTF toolchain inventory required");
        var toolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long toolBytes = 0;
        foreach (var file in package.Toolchain.GltfFiles)
        {
            Need(IsPortablePath(file.Path) && toolNames.Add(file.Path) && IsHash(file.Sha256) && file.Bytes is >= 0 and <= 16_777_216
                && file.Bytes <= 134_217_728 - toolBytes, "glTF toolchain file invalid");
            toolBytes += file.Bytes;
        }
        Need(package.Toolchain.GltfFiles.Any(f => f.Path == "__init__.py"), "glTF module root missing");
    }
}
