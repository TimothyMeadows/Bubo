using System.Text;
using System.Text.RegularExpressions;
using Bubo.Schematic.Domain;

namespace Bubo.Schematic.Application;

public interface ISchematicPreviewRenderer
{
    IReadOnlyDictionary<string, string> Render(AssemblyDesign design, CancellationToken cancellation = default);
}

/// <summary>Requires the complete review inventory; an arbitrary renderer cannot omit a subsystem sheet.</summary>
public sealed class PreviewCompiler(IAssemblyDesignValidator validator, ISchematicPreviewRenderer renderer)
{
    public static HashSet<string> RequiredFiles(AssemblyDesign design) => new[]
        {
            "orthographic-front.svg", "orthographic-right.svg", "orthographic-back.svg",
            "orthographic-left.svg", "orthographic-top.svg", "orthographic-bottom.svg",
            "exploded.svg", "connections.svg", "bom.svg", "bom.json"
        }
        .Concat(design.Sections.Select(section => $"section-{section.Id}.svg"))
        .Concat(design.Parts.Select(part => $"part-{part.Id}.svg"))
        .ToHashSet(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Compile(AssemblyDesign design, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        validator.RequireValid(design, cancellation);
        var required = RequiredFiles(design);
        if (required.Count - 1 > design.Budgets.MaxDrawings)
            throw new InvalidOperationException("The complete schematic drawing inventory exceeds MaxDrawings.");
        var documents = renderer.Render(design, cancellation);
        if (documents is null || !required.SetEquals(documents.Keys))
            throw new InvalidOperationException("Schematic preview must contain all orthographic, exploded, section, part-detail, connection and BOM files.");
        var output = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long bytes = 0;
        foreach (var (name, content) in documents.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]{0,159}\\.(svg|json)$", RegexOptions.CultureInvariant)
                || string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("Schematic preview returned an unsafe filename or empty document.");
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(content));
            if (bytes > design.Budgets.MaxPreviewBytes)
                throw new InvalidOperationException("The complete schematic preview exceeds MaxPreviewBytes.");
            output.Add(name, content);
        }
        return output;
    }
}
