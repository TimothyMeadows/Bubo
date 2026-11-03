using Bubo.FloorPlan.Domain;
using Hephaera.AssetProduction;

namespace Bubo.FloorPlan.Infrastructure;

internal sealed class ExportInvocationBudget(IWorkspace workspace, string directory, BuildingPlan plan, long logMaximum)
{
    readonly long bytesMaximum = Math.Min(plan.Budgets.MaxExportBytes, ExportPackageValidator.MaxPayloadBytes);
    long geometryUsed, logUsed;
    internal long RemainingGeometry => plan.Budgets.MaxGeometryChecks - geometryUsed;
    internal long RemainingLog
    {
        get
        {
            var remaining = logMaximum - logUsed;
            if (remaining < 1024) throw new InvalidOperationException("Cumulative export log allowance exhausted");
            return remaining;
        }
    }
    internal long RemainingBytes(CancellationToken cancellation)
    {
        long total = 0; var count = 0;
        ExportFileGuards.NoAlternateStreams(workspace.Resolve(directory), cancellation);
        var directories = new Queue<string>(); directories.Enqueue(workspace.Resolve(directory));
        while (directories.TryDequeue(out var parent))
        foreach (var path in Directory.EnumerateFileSystemEntries(parent))
        {
            cancellation.ThrowIfCancellationRequested();
            if (++count > 16_384) throw new InvalidOperationException("Export attempt inventory ceiling exceeded");
            var admitted = workspace.Resolve(path);
            ExportFileGuards.NoAlternateStreams(admitted, cancellation);
            if ((File.GetAttributes(admitted) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked export attempt entry");
            if (Directory.Exists(admitted)) { directories.Enqueue(admitted); continue; }
            var length = new FileInfo(admitted).Length;
            if (length < 0 || length > bytesMaximum - total) throw new InvalidOperationException("Cumulative export file allowance exceeded");
            total = checked(total + length);
        }
        return bytesMaximum - total;
    }
    internal void Debit(ExportNativeReport report, string log, CancellationToken cancellation)
    {
        var used = report.Measured["geometryChecks"]?.GetValue<long>() ?? throw new InvalidOperationException("Missing measured cumulative geometry work");
        if (used < 0 || used > RemainingGeometry) throw new InvalidOperationException("Cumulative export geometry allowance exceeded");
        geometryUsed = checked(geometryUsed + used);
        var length = new FileInfo(workspace.Resolve(log)).Length;
        if (length < 0 || length > logMaximum - logUsed) throw new InvalidOperationException("Cumulative export log allowance exceeded");
        logUsed = checked(logUsed + length);
        _ = RemainingBytes(cancellation);
    }
}
