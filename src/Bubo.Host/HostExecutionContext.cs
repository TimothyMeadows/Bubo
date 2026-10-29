using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bubo.Host;

/// <summary>
/// Validates the caller's explicit project scope and evidence. These cooperative
/// filesystem checks do not authenticate a caller, interpret policy instructions,
/// establish human acceptance, or provide an operating-system sandbox.
/// </summary>
public sealed class HostExecutionContext
{
    const int MaximumContextBytes = 8 * 1024 * 1024;
    const int MaximumReferences = 4096;
    readonly StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    readonly StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    readonly JsonObject engine;
    readonly JsonArray libraries;
    readonly string[] readRoots;
    readonly string[] writeRoots;
    readonly string[] inputRoots;
    readonly string[] toolRoots;
    readonly Dictionary<string, HostFileReference> references;
    readonly Dictionary<string, HostFileReference> tools;

    public string Root { get; }
    public string TaskId { get; }
    public string ContextPath { get; }
    public string ContextSha256 { get; }
    public string StateRoot { get; }
    public string StagingRoot { get; }
    public string MediaStateRoot { get; }
    public bool HasExplicitMediaStateRoot { get; }
    public bool InspectionOnly { get; }
    public bool MediaOnly { get; }
    public string Pipeline { get; }
    public string StyleSha256 { get; }
    public string? MediaPath { get; }
    public string? MediaSha256 { get; }
    public int TimeoutSeconds { get; }
    public long MaxLogBytes { get; }
    public long? MaxPrivateBytes { get; }
    public bool AllowProvisioning { get; }
    public bool AllowPromotion { get; }
    public JsonObject Engine => engine.DeepClone().AsObject();
    public JsonArray Libraries => libraries.DeepClone().AsArray();
    public IReadOnlyList<HostFileReference> References { get; }
    public IReadOnlyList<HostFileReference> Tools { get; }
    public IReadOnlyList<string> ToolRoots => System.Array.AsReadOnly(toolRoots);

    public static HostExecutionContext Load(string projectRoot, string contextPath)
    {
        var root = CanonicalRoot(projectRoot);
        var relative = ProjectRelative(root, contextPath);
        var full = Full(root, relative);
        CheckComponents(full, false);
        var bytes = ReadBounded(full);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        RejectDuplicateProperties(document.RootElement);
        var value = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject
            ?? throw Invalid("Host context must be a JSON object");
        return new HostExecutionContext(root, relative, Digest(bytes), value);
    }

    HostExecutionContext(string root, string contextPath, string contextHash, JsonObject value)
    {
        Root = root;
        ContextPath = contextPath;
        ContextSha256 = contextHash;
        if (Text(value, "schemaVersion") != "bubo-host-context/v1")
            throw Invalid("Unsupported host context schemaVersion");
        if (!CanonicalRoot(Text(value, "projectRoot")).Equals(Root, comparison))
            throw Invalid("Host context projectRoot does not match the supplied project root");
        TaskId = Text(value, "task");
        if (TaskId.Length > 80 || !Regex.IsMatch(TaskId, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant))
            throw Invalid("Invalid host task identity");

        var roots = Object(value, "roots");
        StateRoot = RootPath(Text(roots, "state"));
        StagingRoot = RootPath(Text(roots, "staging"));
        toolRoots = RootArray(roots, "tools");
        inputRoots = RootArray(roots, "inputs");
        var outputRoots = RootArray(roots, "outputs");
        HasExplicitMediaStateRoot = roots.ContainsKey("mediaState");
        MediaStateRoot = HasExplicitMediaStateRoot ? RootPath(Text(roots, "mediaState")) : StateRoot + "/media-state";
        writeRoots = [StateRoot, StagingRoot, MediaStateRoot, .. toolRoots, .. outputRoots];
        readRoots = [.. writeRoots, .. inputRoots];

        var policy = Object(value, "policy");
        InspectionOnly = policy["purpose"]?.GetValue<string>() == "read-only-blender-inspection";
        MediaOnly = policy["purpose"]?.GetValue<string>() == "local-media";
        if (policy.ContainsKey("purpose") && !InspectionOnly && !MediaOnly)
            throw Invalid("Unknown host policy purpose");
        if (InspectionOnly)
        {
            if (new[] { "pipeline", "styleSha256", "engine", "libraries", "media", "imageStyle" }.Any(policy.ContainsKey) || roots.ContainsKey("mediaState"))
                throw Invalid("Inspection policy must not assert art or engine selections");
            Pipeline = "";
            StyleSha256 = "";
            engine = new JsonObject();
            libraries = new JsonArray();
        }
        else if (MediaOnly)
        {
            if (Text(policy, "pipeline") != "LOCAL" || new[] { "engine", "libraries", "styleSha256" }.Any(policy.ContainsKey))
                throw Invalid("Local-media policy requires LOCAL and does not infer an engine or image style");
            if (policy.ContainsKey("imageStyle") && policy["imageStyle"] is not JsonObject)
                throw Invalid("Explicit imageStyle must be a hash-bound policy object");
            Pipeline = "LOCAL";
            StyleSha256 = policy["imageStyle"] is JsonObject style ? HashText(style, "sha256") : "";
            engine = new JsonObject();
            libraries = new JsonArray();
        }
        else
        {
        Pipeline = Text(policy, "pipeline");
        if (Pipeline is not ("CLOUD" or "LOCAL" or "PROOF_TO_MODEL" or "CODE" or "BLENDER"))
            throw Invalid("Unknown host pipeline selection");
        StyleSha256 = HashText(policy, "styleSha256");
        engine = Object(policy, "engine").DeepClone().AsObject();
        if (Text(engine, "effectiveEngine") is not ("THREEJS" or "BABYLONJS" or "PHASERJS" or "UNITY" or "UNREAL" or "GODOT"))
            throw Invalid("Unknown host engine selection");
        if (engine["outputScope"] is not null)
            throw Invalid("Prototype outputScope is not supported by this execution context");
        foreach (var field in new[] { "definitionSha256", "registrySha256" })
            if (engine.ContainsKey(field))
                _ = HashText(engine, field);
        if (engine.TryGetPropertyValue("contractSha256", out var contractHash))
        {
            var isDefault = engine["selectionSource"] is JsonValue source
                && source.TryGetValue<string>(out var selectionSource) && selectionSource == "default";
            if (contractHash is not null || !isDefault)
                _ = HashText(engine, "contractSha256");
        }
        libraries = policy.TryGetPropertyValue("libraries", out var libraryValue)
            ? libraryValue?.DeepClone() as JsonArray ?? throw Invalid("policy.libraries must be an array")
            : [];
        ValidateLibraries(libraries);
        }

        references = new Dictionary<string, HostFileReference>(comparer);
        foreach (var entry in Array(value, "references", true))
        {
            var item = entry as JsonObject ?? throw Invalid("A host reference must be an object");
            var path = ProjectRelative(Root, Text(item, "path"));
            var kind = Text(item, "kind");
            if (kind is not ("rights" or "consent" or "review" or "policy" or "input"))
                throw Invalid("Unknown host reference kind");
            if (path.Equals(ContextPath, comparison))
                throw Invalid("The host context cannot hash-reference itself");
            if (!references.TryAdd(path, new(path, HashText(item, "sha256"), kind)))
                throw Invalid("Duplicate host reference identity");
        }
        tools = new Dictionary<string, HostFileReference>(comparer);
        foreach (var entry in Array(value, "tools", true))
        {
            var item = entry as JsonObject ?? throw Invalid("A host tool must be an object");
            var path = AbsoluteToolPath(Root, Text(item, "path"));
            var kind = item.TryGetPropertyValue("kind", out _) ? Text(item, "kind") : "tool";
            if (!tools.TryAdd(path, new(path, HashText(item, "sha256"), kind)))
                throw Invalid("Duplicate host tool identity");
            if (IsWithin(path, Root) && references.ContainsKey(ProjectRelative(Root, path)))
                throw Invalid("A file cannot be duplicated as host reference and tool");
        }
        References = new ReadOnlyCollection<HostFileReference>(references.Values.ToArray());
        Tools = new ReadOnlyCollection<HostFileReference>(tools.Values.ToArray());

        if (InspectionOnly)
        {
            var receipt = Object(policy, "receipt");
            var receiptPath = ProjectRelative(Root, Text(receipt, "path"));
            if (!references.TryGetValue(receiptPath, out var reference) || reference.Kind != "policy" || reference.Sha256 != HashText(receipt, "sha256"))
                throw Invalid("Inspection policy receipt must match a registered policy reference");
        }

        if (policy.TryGetPropertyValue("media", out var mediaValue))
        {
            var media = mediaValue as JsonObject ?? throw Invalid("policy.media must be an object");
            MediaPath = ProjectRelative(Root, Text(media, "path"));
            MediaSha256 = HashText(media, "sha256");
            if (!references.TryGetValue(MediaPath, out var reference) || reference.Kind != "policy" || reference.Sha256 != MediaSha256)
                throw Invalid("Media policy must match a registered policy reference");
        }
        if ((MediaOnly || roots.ContainsKey("mediaState")) && (MediaPath is null || Pipeline != "LOCAL"))
            throw Invalid("Shared media state and local-media purpose require an explicit hash-bound LOCAL media policy");
        if (MediaOnly && policy["imageStyle"] is JsonObject imageStyle)
        {
            var stylePath = ProjectRelative(Root, Text(imageStyle, "path"));
            if (!references.TryGetValue(stylePath, out var styleReference) || styleReference.Kind != "policy" || styleReference.Sha256 != StyleSha256)
                throw Invalid("Image style must match an exact registered policy reference");
        }

        var budgets = Object(value, "budgets");
        var seconds = PositiveInteger(budgets, "timeoutSeconds");
        if (seconds > int.MaxValue)
            throw Invalid("Host timeoutSeconds exceeds the supported integer bound");
        TimeoutSeconds = (int)seconds;
        MaxLogBytes = PositiveInteger(budgets, "maxLogBytes");
        if (budgets.TryGetPropertyValue("maxPrivateBytes", out _))
            MaxPrivateBytes = PositiveInteger(budgets, "maxPrivateBytes");
        var permissions = value.TryGetPropertyValue("permissions", out var permissionsValue)
            ? permissionsValue as JsonObject ?? throw Invalid("permissions must be an object")
            : new JsonObject();
        AllowProvisioning = Boolean(permissions, "provisioning");
        AllowPromotion = Boolean(permissions, "promotion");
        if (InspectionOnly && (AllowProvisioning || AllowPromotion))
            throw Invalid("Read-only inspection cannot authorize provisioning or promotion");
        if (MediaOnly && AllowPromotion)
            throw Invalid("Media execution cannot grant asset promotion; use separate reviewed host acceptance");

        ValidateFresh();
        foreach (var tool in tools.Values)
            VerifyFile(tool.Path, tool.Sha256, "Registered tool");
    }

    /// <summary>Check the originally loaded identities; never reload changed expectations.</summary>
    public void ValidateFresh()
    {
        CheckComponents(Root, false);
        var context = Full(Root, ContextPath);
        CheckComponents(context, false);
        if (Digest(ReadBounded(context)) != ContextSha256)
            throw Invalid("Host context hash changed; refresh affected evidence explicitly");
        foreach (var reference in references.Values)
            VerifyFile(Full(Root, reference.Path), reference.Sha256, "Host reference");
    }

    public void RequireReference(string path, string? kind = null)
    {
        var relative = ProjectRelative(Root, path);
        if (!references.TryGetValue(relative, out var reference) || (kind is not null && reference.Kind != kind))
            throw Invalid("A matching registered host reference is required");
        VerifyFile(Full(Root, reference.Path), reference.Sha256, "Host reference");
    }

    public void EnsureReadAllowed(string path) => _ = ResolveReadPath(path);

    /// <summary>Admit a read and return the same checked canonical full path.</summary>
    public string ResolveReadPath(string path)
    {
        var relative = ProjectRelative(Root, path);
        var full = Full(Root, relative);
        CheckComponents(full, true);
        if (relative.Equals(ContextPath, comparison) || references.ContainsKey(relative) || tools.ContainsKey(full)
            || readRoots.Any(scope => IsWithin(full, Full(Root, scope))))
            return full;
        throw Invalid("Read is outside the host-declared roots and exact references");
    }

    public void EnsureWriteAllowed(string path) => _ = ResolveWritePath(path);

    /// <summary>Admit a write and return the same checked canonical full path.</summary>
    public string ResolveWritePath(string path)
    {
        var relative = ProjectRelative(Root, path);
        var full = Full(Root, relative);
        CheckComponents(full, true);
        if (relative.Equals(ContextPath, comparison) || references.ContainsKey(relative) || tools.ContainsKey(full)
            || inputRoots.Any(scope => IsWithin(full, Full(Root, scope))))
            throw Invalid("Host context, registered evidence/tools and input-only roots are read-only");
        if (writeRoots.Any(scope => IsWithin(full, Full(Root, scope))))
            return full;
        throw Invalid("Write is outside the host-declared writable roots");
    }

    public void EnsureToolAllowed(string absolutePath) => _ = HashToolFile(absolutePath);

    /// <summary>Admit an exact registered tool and return its freshly verified digest.</summary>
    public string HashToolFile(string absolutePath)
    {
        if (!Path.IsPathFullyQualified(Separators(absolutePath)))
            throw Invalid("An absolute registered executable is required");
        var full = AbsoluteToolPath(Root, absolutePath);
        if (!tools.TryGetValue(full, out var tool))
            throw Invalid("Native executable is not registered in the host context");
        return VerifyFile(tool.Path, tool.Sha256, "Registered tool");
    }

    string RootPath(string path)
    {
        if (Path.IsPathRooted(Separators(path)))
            throw Invalid("Declared roots must be project-relative");
        var relative = ProjectRelative(Root, path);
        var full = Full(Root, relative);
        CheckComponents(full, true);
        if (File.Exists(full) && !Directory.Exists(full))
            throw Invalid("A declared directory root cannot identify an existing file");
        return relative;
    }

    string[] RootArray(JsonObject value, string key)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(comparer);
        foreach (var entry in Array(value, key, true))
        {
            var root = RootPath(String(entry, "Declared root"));
            if (!seen.Add(root))
                throw Invalid("Duplicate declared root identity");
            result.Add(root);
        }
        return [.. result];
    }

    static string CanonicalRoot(string path)
    {
        CheckSyntax(path);
        var normalized = Separators(path);
        if (!Path.IsPathFullyQualified(normalized))
            throw Invalid("projectRoot must be an absolute existing directory");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(normalized));
        CheckComponents(full, false);
        if (!Directory.Exists(full))
            throw Invalid("projectRoot must be an existing directory");
        return full;
    }

    static string ProjectRelative(string root, string path)
    {
        CheckSyntax(path);
        var normalized = Separators(path);
        if (Path.IsPathRooted(normalized) && !Path.IsPathFullyQualified(normalized))
            throw Invalid("Drive-relative and partially rooted paths are unsupported");
        if (!Path.IsPathRooted(normalized) && normalized.Contains(':'))
            throw Invalid("Colon-qualified relative paths are unsupported");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(normalized, root));
        if (!IsWithin(full, root) || full.Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw Invalid("Path escapes or selects the whole project root");
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (relative.Split('/').Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw Invalid("Git metadata is reserved");
        return relative;
    }

    static string AbsoluteToolPath(string root, string path)
    {
        CheckSyntax(path);
        var normalized = Separators(path);
        if (Path.IsPathRooted(normalized) && !Path.IsPathFullyQualified(normalized))
            throw Invalid("Drive-relative tool paths are unsupported");
        if (!Path.IsPathRooted(normalized) && normalized.Contains(':'))
            throw Invalid("Colon-qualified relative tool paths are unsupported");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(normalized, root));
        if (!Path.IsPathRooted(normalized) && !IsWithin(full, root))
            throw Invalid("Relative tool path escapes the project");
        var segments = Path.GetRelativePath(Path.GetPathRoot(full)!, full).Split(Path.DirectorySeparatorChar);
        if (segments.Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw Invalid("Git metadata is reserved");
        CheckComponents(full, false);
        if (!File.Exists(full))
            throw Invalid("Registered native tool must be an existing file");
        return full;
    }

    static void CheckSyntax(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
            throw Invalid("Paths must be nonempty single-line strings");
        var deviceForm = path.Replace('/', '\\');
        if (deviceForm.StartsWith("\\\\?\\", StringComparison.Ordinal) || deviceForm.StartsWith("\\\\.\\", StringComparison.Ordinal))
            throw Invalid("Device namespace paths are unsupported");
        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".."))
            throw Invalid("Path traversal segments are prohibited");
        if (parts.Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw Invalid("Git metadata is reserved");
        foreach (var part in parts)
        {
            if (part.Contains(':') && !(OperatingSystem.IsWindows() && part.Length == 2 && char.IsAsciiLetter(part[0]) && part[1] == ':'))
                throw Invalid("Alternate-stream or colon-qualified paths are unsupported");
            if (OperatingSystem.IsWindows() && (part.EndsWith(' ') || part.EndsWith('.')))
                throw Invalid("Windows trailing-dot/space path aliases are unsupported");
            if (OperatingSystem.IsWindows())
            {
                var stem = part.Split('.')[0].ToUpperInvariant();
                if (stem is "CON" or "PRN" or "AUX" or "NUL" || Regex.IsMatch(stem, "^(COM|LPT)[1-9]$", RegexOptions.CultureInvariant))
                    throw Invalid("Windows reserved device names are unsupported");
            }
        }
    }

    static string Separators(string path) => path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    static string Full(string root, string relative) => Path.GetFullPath(Separators(relative), root);

    static bool IsWithin(string candidate, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(directory);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return candidate.Equals(root, comparison) || candidate.StartsWith(prefix, comparison);
    }

    static void CheckComponents(string path, bool allowMissing)
    {
        var full = Path.GetFullPath(path);
        var volume = Path.GetPathRoot(full) ?? throw Invalid("Missing filesystem root");
        var current = volume;
        Check(current);
        var segments = Path.GetRelativePath(volume, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".").ToArray();
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!Check(current))
                break;
            if (index < segments.Length - 1 && !Directory.Exists(current))
                throw Invalid("An existing path ancestor must be a directory");
        }

        bool Check(string component)
        {
            try
            {
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                    throw Invalid("Symbolic links and reparse components are prohibited");
                return true;
            }
            catch (FileNotFoundException) when (allowMissing) { return false; }
            catch (DirectoryNotFoundException) when (allowMissing) { return false; }
        }
    }

    static string VerifyFile(string path, string expected, string label)
    {
        CheckComponents(path, false);
        if (!File.Exists(path))
            throw Invalid(label + " is missing or is not a regular file");
        using var stream = File.OpenRead(path);
        var observed = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (observed != expected)
            throw Invalid(label + " hash changed; refresh affected evidence explicitly");
        return observed;
    }

    static byte[] ReadBounded(string path)
    {
        using var stream = File.OpenRead(path);
        var initialLength = stream.Length;
        if (initialLength is < 2 or > MaximumContextBytes)
            throw Invalid("Host context is missing or exceeds the 8 MiB JSON bound");
        using var buffer = new MemoryStream((int)initialLength);
        var chunk = new byte[65536];
        int count;
        while ((count = stream.Read(chunk, 0, Math.Min(chunk.Length, MaximumContextBytes + 1 - (int)buffer.Length))) > 0)
        {
            buffer.Write(chunk, 0, count);
            if (buffer.Length > MaximumContextBytes)
                throw Invalid("Host context exceeds the 8 MiB JSON bound");
        }
        return buffer.ToArray();
    }

    static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw Invalid("Duplicate host-context JSON property");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray())
                RejectDuplicateProperties(value);
        }
    }

    static void ValidateLibraries(JsonArray value)
    {
        if (value.Count > MaximumReferences)
            throw Invalid("Too many declared asset libraries");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in value)
        {
            var library = entry as JsonObject ?? throw Invalid("A declared library must be an object");
            if (!ids.Add(Text(library, "id")))
                throw Invalid("Duplicate declared library identity");
            _ = Text(library, "root");
        }
    }

    static JsonObject Object(JsonObject value, string key) => value[key] as JsonObject ?? throw Invalid(key + " must be an object");
    static JsonArray Array(JsonObject value, string key, bool optional)
    {
        if (!value.TryGetPropertyValue(key, out var node) && optional)
            return [];
        var array = node as JsonArray ?? throw Invalid(key + " must be an array");
        if (array.Count > MaximumReferences)
            throw Invalid(key + " exceeds the supported item bound");
        return array;
    }
    static string String(JsonNode? value, string label)
    {
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl))
            throw Invalid(label + " must be a nonempty single-line string");
        return text;
    }
    static string Text(JsonObject value, string key) => String(value[key], key);
    static string HashText(JsonObject value, string key)
    {
        var hash = Text(value, key);
        if (!Regex.IsMatch(hash, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            throw Invalid(key + " must be a lowercase SHA-256 identity");
        return hash;
    }
    static long PositiveInteger(JsonObject value, string key)
    {
        if (value[key] is not JsonValue number || !number.TryGetValue<long>(out var result) || result < 1)
            throw Invalid(key + " must be a finite positive supported integer");
        return result;
    }
    static bool Boolean(JsonObject value, string key)
    {
        if (!value.TryGetPropertyValue(key, out var node))
            return false;
        if (node is not JsonValue scalar || !scalar.TryGetValue<bool>(out var result))
            throw Invalid(key + " must be a boolean");
        return result;
    }
    static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static InvalidOperationException Invalid(string message) => new(message);
}
