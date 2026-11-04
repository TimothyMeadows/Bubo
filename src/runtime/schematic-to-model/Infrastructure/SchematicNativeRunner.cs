using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using Hephaera.AssetProduction;

namespace Bubo.Schematic.Infrastructure;

/// <summary>Fixed reviewed resources and one cumulative bounded native operation; caller owns the asset lock.</summary>
internal sealed class SchematicNativeRunner(IWorkspace workspace, INativeProcess process, string bundleDirectory,
    string catalogSha256, int timeoutSeconds, long maxLogBytes, long? maxPrivateBytes)
{
    internal sealed class Budget(int seconds, long bytes)
    {
        readonly Stopwatch elapsed = Stopwatch.StartNew();
        long logRemaining = bytes;
        public int Seconds()
        {
            var remaining = Math.Ceiling(seconds - elapsed.Elapsed.TotalSeconds);
            if (remaining < 1) throw new TimeoutException("Cumulative schematic native operation deadline exhausted");
            return checked((int)remaining);
        }
        public long LogBytes()
        {
            if (logRemaining < 1024) throw new InvalidOperationException("Cumulative schematic native log budget exhausted");
            return logRemaining;
        }
        public void Debit(string log)
        {
            logRemaining = checked(logRemaining - new FileInfo(log).Length);
            if (logRemaining < 0) throw new InvalidOperationException("Cumulative schematic native logs exceeded approved bytes");
            _ = Seconds();
        }
    }
    internal static bool IsHash(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    internal static byte[] Capture(string path, int maximum)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Missing or linked reviewed schematic file");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 || stream.Length > maximum) throw new InvalidOperationException("Reviewed schematic file exceeds its finite capture bound");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidOperationException("Reviewed schematic file grew during capture");
        return bytes;
    }
    internal void RequireBudget()
    {
        if (maxPrivateBytes is null) throw new InvalidOperationException("Schematic native execution requires an explicit aggregate committed-memory ceiling");
        if (!process.SupportsPrivateMemoryLimit) throw new PlatformNotSupportedException("Schematic assembly requires supported 64-bit Windows owned-job memory enforcement; no unbounded launch");
        if (maxPrivateBytes is < 67_108_864 or > 68_719_476_736 || timeoutSeconds is < 1 or > 86400 || maxLogBytes is < 1024 or > 1_073_741_824)
            throw new InvalidOperationException("Invalid explicit schematic native process budget");
    }
    internal string ToolHash(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("An explicit registered existing absolute Blender executable is required");
        return workspace.HostContext?.HashToolFile(executable) ?? workspace.HashFile(executable);
    }
    internal SortedDictionary<string, string> Resources(IReadOnlyList<string> scripts)
    {
        var catalog = Path.Combine(Directory.GetParent(bundleDirectory)?.Parent?.FullName ?? throw new InvalidOperationException("Invalid schematic resource layout"), "catalog.json");
        var catalogBytes = Capture(catalog, 1_048_576);
        if (!IsHash(catalogSha256) || Convert.ToHexStringLower(SHA256.HashData(catalogBytes)) != catalogSha256)
            throw new InvalidOperationException("Schematic resource catalog differs from its compiled reviewed identity");
        using var document = JsonDocument.Parse(catalogBytes);
        var files = document.RootElement.GetProperty("files");
        var bundle = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if ((File.GetAttributes(bundleDirectory) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Schematic bundle directory cannot be linked");
        foreach (var name in scripts)
        {
            if (Path.GetFileName(name) != name || !name.EndsWith(".py", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid fixed schematic script identity");
            var path = Path.Combine(bundleDirectory, name);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length is <= 0 or > 2_097_152)
                throw new InvalidOperationException("Fixed schematic bundle file is missing, linked or excessive");
            var hash = Convert.ToHexStringLower(SHA256.HashData(Capture(path, 2_097_152)));
            if (!files.TryGetProperty("resources/schematic-to-model/blender/" + name, out var expected) || expected.GetString() != hash)
                throw new InvalidOperationException("Schematic bundle differs from reviewed catalog: " + name);
            bundle.Add(name, hash);
        }
        return bundle;
    }
    internal void FreshResources(SortedDictionary<string, string> expected)
    {
        var actual = Resources(expected.Keys.ToArray());
        if (actual.Count != expected.Count || expected.Any(p => actual.GetValueOrDefault(p.Key) != p.Value))
            throw new InvalidOperationException("Reviewed schematic resources changed during execution");
    }
    internal void WriteNew(string relative, byte[] bytes)
    {
        var path = workspace.ResolveWrite(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes); stream.Flush(true);
    }
    internal void CopyBundle(string directory, SortedDictionary<string, string> bundle)
    {
        foreach (var (name, hash) in bundle)
        {
            var bytes = Capture(Path.Combine(bundleDirectory, name), 2_097_152);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash) throw new InvalidOperationException("Reviewed schematic script changed during immutable copy");
            WriteNew(directory + "/bundle/" + name, bytes);
        }
        FreshResources(bundle); FreshCopy(directory, bundle);
    }
    internal void FreshCopy(string directory, SortedDictionary<string, string> bundle)
    {
        foreach (var (name, hash) in bundle)
            if (PartOutputVerifier.FileHash(workspace, directory + "/bundle/" + name, maximumBytes: 2_097_152) != hash)
                throw new InvalidOperationException("Immutable schematic working bundle changed");
        var entries = Directory.EnumerateFileSystemEntries(workspace.Resolve(directory + "/bundle")).Take(bundle.Count + 1).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        if (!entries.SetEquals(bundle.Keys)) throw new InvalidOperationException("Immutable schematic working bundle inventory differs");
    }
    Dictionary<string, string> EnvironmentFor(string directory) => new(StringComparer.Ordinal)
    {
        ["TEMP"] = directory, ["TMP"] = directory, ["TMPDIR"] = directory, ["PYTHONDONTWRITEBYTECODE"] = "1", ["PYTHONNOUSERSITE"] = "1",
        ["XDG_CACHE_HOME"] = Path.Combine(directory, "cache"), ["BLENDER_USER_CONFIG"] = Path.Combine(directory, "config"),
        ["BLENDER_USER_SCRIPTS"] = Path.Combine(directory, "scripts"), ["BLENDER_USER_DATAFILES"] = Path.Combine(directory, "data"), ["BLENDER_USER_EXTENSIONS"] = Path.Combine(directory, "extensions")
    };
    internal async Task<string> Version(string executable, string directory, Budget budget, CancellationToken cancellation)
    {
        RequireBudget(); Directory.CreateDirectory(workspace.ResolveWrite(directory + "/temp"));
        var log = workspace.ResolveWrite(directory + "/version.log");
        await process.RunWithLimits(executable, ["--version"], workspace.Root, log, Math.Min(30, budget.Seconds()), maxPrivateBytes!.Value,
            cancellation, Math.Min(16384, budget.LogBytes()), EnvironmentFor(workspace.Resolve(directory + "/temp")));
        budget.Debit(log);
        var match = Regex.Match(File.ReadAllText(log), "Blender (4\\.5\\.[0-9]+(?: LTS)?)");
        if (!match.Success) throw new InvalidOperationException("Selected tool did not report Blender 4.5 LTS");
        return match.Groups[1].Value;
    }
    internal async Task Run(string executable, string directory, string script, string jobSha256, SortedDictionary<string, string> bundle,
        string logName, Budget budget, CancellationToken cancellation)
    {
        RequireBudget();
        if (!bundle.ContainsKey(script) || Path.GetFileName(logName) != logName || !IsHash(jobSha256)) throw new InvalidOperationException("Invalid fixed schematic launch");
        FreshCopy(directory, bundle);
        if (PartOutputVerifier.FileHash(workspace, directory + "/job.json", cancellation, 8_388_608) != jobSha256)
            throw new InvalidOperationException("Immutable schematic job changed before launch");
        var log = workspace.ResolveWrite(directory + "/" + logName);
        await process.RunWithLimits(executable, ["--background", "--factory-startup", "--disable-autoexec", "--offline-mode", "--python-exit-code", "1", "--python",
            workspace.Resolve(directory + "/bundle/" + script), "--", workspace.Resolve(directory + "/job.json"), jobSha256], workspace.Root, log,
            budget.Seconds(), maxPrivateBytes!.Value, cancellation, budget.LogBytes(), EnvironmentFor(workspace.Resolve(directory + "/temp")));
        budget.Debit(log); FreshCopy(directory, bundle);
        if (PartOutputVerifier.FileHash(workspace, directory + "/job.json", cancellation, 8_388_608) != jobSha256)
            throw new InvalidOperationException("Immutable schematic job changed during native execution");
    }
}
