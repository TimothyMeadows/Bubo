using System.Security.Cryptography;
using System.Text;
using Bubo.FloorPlan.Domain;
namespace Bubo.FloorPlan.Application;

public interface IArchitecturalPreviewRenderer
{
    IReadOnlyDictionary<string, string> Render(BuildingPlan plan, CancellationToken cancellationToken = default);
}

public sealed record PreviewCompilation(ValidationReport Validation, IReadOnlyDictionary<string, string> Sheets,
    IReadOnlyDictionary<string, string> DrawingHashes, long TotalBytes);

public sealed class PreviewCompiler(IBuildingPlanValidator validator, IArchitecturalPreviewRenderer renderer)
{
    public PreviewCompilation Compile(BuildingPlan plan, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = validator.Validate(plan, cancellationToken);
        if (!validation.IsValid) throw new PlanValidationException(validation);
        var sheets = renderer.Render(plan, cancellationToken);
        var expected = Enumerable.Range(1, plan.Storeys.Count).Select(i => $"floor-{i:D2}.svg")
            .Concat(new[] { "roof-plan.svg", "elevation-north.svg", "elevation-south.svg", "elevation-east.svg", "elevation-west.svg", "section-principal.svg" })
            .Concat(Enumerable.Range(1, plan.Stairs.Count).Select(i => $"section-stair-{i:D2}.svg")).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(sheets.Keys)) throw new InvalidOperationException("Preview must include every floor, roof, four elevations, principal section and every stair section");
        long total = 0;
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, text) in sheets.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Empty architectural preview");
            var bytes = Encoding.UTF8.GetBytes(text);
            total = checked(total + bytes.Length);
            if (total > plan.Budgets.MaxPreviewBytes) throw new InvalidOperationException("Architectural previews exceed the declared byte budget");
            hashes.Add(name, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        return new(validation, sheets, hashes, total);
    }
}
