using System.Text.Json;
using System.Text.RegularExpressions;
using Hephaera.AssetProduction;

namespace Bubo.FloorPlan.Infrastructure;

internal sealed class ExportResources(IWorkspace workspace, string directory, string catalogSha256)
{
    internal static readonly string[] Names = ["admission.py", "export.py", "export_admission.py", "export_geometry.py", "export_views.py", "review.py"];

    internal SortedDictionary<string, string> Capture()
    {
        if (!Regex.IsMatch(catalogSha256, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("Missing compiled export catalog identity");
        var resourceRoot = Directory.GetParent(directory)?.Parent?.FullName ?? throw new InvalidOperationException("Invalid fixed export bundle layout");
        var catalogPath = Path.Combine(resourceRoot, "catalog.json");
        var info = new FileInfo(catalogPath);
        if (!info.Exists || info.Length > 1_048_576 || (info.Attributes & FileAttributes.ReparsePoint) != 0 || workspace.HashFile(catalogPath) != catalogSha256)
            throw new InvalidOperationException("Export resources differ from the compiled reviewed catalog");
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(catalogPath));
        var files = catalog.RootElement.GetProperty("files");
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Names)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Fixed export resource is missing or linked: " + name);
            var hash = workspace.HashFile(path);
            if (!files.TryGetProperty("resources/floor-plan-to-model/blender/" + name, out var declared) || declared.GetString() != hash)
                throw new InvalidOperationException("Fixed export resource is not registered with exact reviewed bytes: " + name);
            result.Add(name, hash);
        }
        return result;
    }

    internal void RequireCurrent(SortedDictionary<string, string> expected)
    {
        var current = Capture();
        if (current.Count != expected.Count || current.Any(p => expected.GetValueOrDefault(p.Key) != p.Value))
            throw new InvalidOperationException("Fixed export bundle changed during the operation");
    }
}
