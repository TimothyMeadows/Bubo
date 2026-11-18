using System.Text.Json.Nodes;
using Bubo.Host;
using HostExecutionContext = Bubo.Host.HostExecutionContext;

namespace Bubo.Media.Infrastructure;

/// <summary>Content-only reuse of the existing archive/wheel verifier. Selectors and provisioning stay with the caller.</summary>
public static class StandalonePythonIntegrity
{
    public static void VerifyLoadedModules(HostExecutionContext host,IEnumerable<string> installedFiles,string[] modules)
    {
        if(modules.Length is <1 or >2048 || modules.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=modules.Length)
            throw new InvalidOperationException("Missing/unbounded/duplicate observed native modules");
        if(!OperatingSystem.IsWindows())return; // Native Linux verification remains unavailable; CPU fixtures do not certify a loader.
        var installed=installedFiles.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var controlledNames=installed.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);var system=Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach(var module in modules)
        {
            if(!Path.IsPathFullyQualified(module))throw new InvalidOperationException("Absolute loaded-module observation required");
            var path=Path.GetFullPath(module);if(installed.Contains(path))continue;
            if(controlledNames.Contains(Path.GetFileName(path)))throw new InvalidOperationException("Observed module shadows its pinned dependency");
            if(Bubo.Media.Application.MediaAdmission.Within(path,system) || Bubo.Media.Application.MediaAdmission.Within(path,Path.Combine(windows,"WinSxS")))continue;
            var platform=host.Tools.SingleOrDefault(x=>x.Kind=="platform-module" && x.Path.Equals(path,StringComparison.OrdinalIgnoreCase));
            if(platform is not null && host.HashToolFile(path)==platform.Sha256)continue;
            throw new InvalidOperationException("Observed module is outside its pinned installation/system-driver boundary");
        }
    }
    public static void Verify(HostExecutionContext host, string environment, string pythonBase,
        string wheelhouse, string pythonArchive, JsonObject specification, JsonObject runtime, JsonObject catalog)
    {
        host.ValidateFresh();
        PythonIntegrity.Verify(host, environment, pythonBase, wheelhouse, pythonArchive, specification, runtime, catalog);
        // PythonIntegrity admits this generated filename. Its configuration is a separate trust boundary.
        var configuration = host.ResolveReadPath(Path.Combine(environment, "pyvenv.cfg"));
        if (!File.Exists(configuration) || new FileInfo(configuration).Length is < 1 or > 16_384)
            throw new InvalidOperationException("Bounded isolated Python configuration required");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(configuration))
        {
            if (!line.Contains('=')) continue;
            var pair = line.Split('=', 2);
            if (!values.TryAdd(pair[0].Trim(), pair[1].Trim()))
                throw new InvalidOperationException("Duplicate isolated Python configuration key");
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var expectedHome = OperatingSystem.IsWindows() ? pythonBase : Path.Combine(pythonBase, "bin");
        if (!values.TryGetValue("include-system-site-packages", out var include) || include != "false"
            || !values.TryGetValue("home", out var home) || !Path.IsPathFullyQualified(home)
            || !Path.TrimEndingDirectorySeparator(Path.GetFullPath(home)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedHome)), comparison))
            throw new InvalidOperationException("Python environment must exclude system packages and bind its verified base");
        host.ValidateFresh();
    }
}
