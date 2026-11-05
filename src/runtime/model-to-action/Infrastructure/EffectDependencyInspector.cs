using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Vfx.Application;
using Bubo.Vfx.Domain;
using Bubo.Vfx.Infrastructure;
using Hephaera.AssetProduction;
using Hephaera.ModelAction.Application;
using Hephaera.ModelAction.Domain;
using static Hephaera.ModelAction.Application.ActionEffectDependencies;
using static Hephaera.ModelAction.Application.ActionValidation;

namespace Hephaera.ModelAction.Infrastructure;

/// <summary>Read-only reuse of the native VFX validators. No renderer, provider or executable input is admitted.</summary>
public sealed class EffectDependencyInspector : IActionEffectDependencyInspector
{
    public ActionEffectInspection Inspect(IWorkspace workspace, ActionEffectSource source,
        ActionEffectInspectionContext context, CancellationToken cancellation)
        => InspectCore(workspace, source, context, cancellation, false, out _);

    public ActionEffectReviewSubject ReviewSubject(IWorkspace workspace, ActionEffectSource source,
        ActionEffectInspectionContext context, CancellationToken cancellation)
    {
        var inspected = InspectCore(workspace, source, context, cancellation, true, out var subject);
        return new(source.Id, subject, inspected.SourceInventory.Files);
    }

    ActionEffectInspection InspectCore(IWorkspace workspace, ActionEffectSource source,
        ActionEffectInspectionContext context, CancellationToken cancellation, bool draft, out string reviewSubject)
    {
        cancellation.ThrowIfCancellationRequested();
        workspace.HostContext?.ValidateFresh();
        Id(source.Id); Id(source.EffectId); Hash(context.StyleSha256); Hash(context.RuntimeBuildSha256);
        if (source.PreparationContext is not null && source.Preparation is null) throw new InvalidOperationException("A preparation context requires an exact native preparation seal");
        var files = new Dictionary<string, ActionEffectFile>(StringComparer.OrdinalIgnoreCase);
        var maxFiles = Integer(context.Budgets, "maxEffectFiles", 1, 16384);
        var maxBytes = LongBudget(context.Budgets, "maxEffectDependencyBytes", 1, 4L * 1024 * 1024 * 1024);
        var maxJson = LongBudget(context.Budgets, "maxEffectJsonBytes", 1, 256L * 1024 * 1024);
        long total = 0, jsonTotal = 0;

        ActionEffectFile Add(string role, string path, string? expected = null, long maximum = MaximumFileBytes,
            string? resourceId = null, long? length = null, string? referenceKind = null)
        {
            cancellation.ThrowIfCancellationRequested();
            Resolve(workspace, path);
            if (expected is not null) Hash(expected);
            if (referenceKind is not null) workspace.HostContext?.RequireReference(path, referenceKind);
            if (files.TryGetValue(path, out var previous))
            {
                if (previous.Path != path || expected is not null && previous.Sha256 != expected || length.HasValue && previous.Bytes != length)
                    throw new InvalidOperationException("Effect input aliases a conflicting source identity");
                return previous;
            }
            var size = new FileInfo(Resolve(workspace, path)).Length;
            var isJson = IsJsonFile(role, path);
            if (size < 1 || size > maximum || size > maxBytes - total || isJson && size > maxJson - jsonTotal || files.Count >= maxFiles || length.HasValue && size != length)
                throw new InvalidOperationException("Effect source exceeds remaining file/byte/JSON allowance before read");
            var actual = Observe(workspace, path, Math.Min(maximum, maxBytes - total), cancellation);
            if (expected is not null && actual.Sha256 != expected || length.HasValue && actual.Bytes != length)
                throw new InvalidOperationException("Effect source declaration does not match exact file bytes");
            var file = actual with { Role = role, SourceId = source.Id, ResourceId = resourceId };
            files.Add(path, file); total = checked(total + file.Bytes);
            if (isJson) jsonTotal = checked(jsonTotal + file.Bytes);
            return file;
        }
        byte[] JsonFile(string role, ActionFileReference reference, string? referenceKind = "input")
        {
            if (!reference.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Typed effect JSON references require a JSON file extension");
            Add(role, reference.Path, reference.Sha256, MaximumJsonBytes, referenceKind: referenceKind);
            return Read(workspace, reference.Path, MaximumJsonBytes, cancellation);
        }

        var packageBytes = JsonFile("native-package", source.Package);
        var declared = VfxJson.Parse(packageBytes);
        var declaredValidation = VfxValidation.Validate(declared);
        if (!declaredValidation.IsValid) throw new InvalidOperationException("Effect source native package declaration is invalid");
        var directory = Path.GetDirectoryName(source.Package.Path)!.Replace('\\', '/');
        // Reserve the finite native inventory before the VFX inspector opens resources.
        if (declared.Resources.Count > maxFiles - files.Count || declared.Resources.Sum(resource => resource.Bytes) > maxBytes - total)
            throw new InvalidOperationException("Native effect inventory exceeds remaining source closure allowance");
        foreach (var resource in declared.Resources)
        {
            var path = directory.Length == 0 ? resource.Path : directory + "/" + resource.Path;
            var size = new FileInfo(Resolve(workspace, path)).Length;
            if (size != resource.Bytes || size < 1 || size > MaximumFileBytes)
                throw new InvalidOperationException("Native effect resource lacks its finite declared length");
        }
        var inspected = VfxInspection.Inspect(workspace, source.Package.Path, cancellation);
        if (!inspected.IsValid || inspected.PackageSha256 != source.Package.Sha256) throw new InvalidOperationException("Effect source native package failed current inspection");
        RequireEngine(inspected.Package, context.Engine, context.StyleSha256);
        if (!inspected.Package.Effects.Any(effect => effect.Id == source.EffectId)) throw new InvalidOperationException("Selected effect is missing from its exact native package");
        var resources = inspected.ObservedResources.Select(item => new ActionEffectResource { Id = item.Id, Kind = item.Kind, Path = item.ProjectPath, Sha256 = item.Sha256, Bytes = item.Bytes }).ToArray();
        foreach (var resource in resources) Add("resource", resource.Path, resource.Sha256, MaximumFileBytes, resource.Id, resource.Bytes);

        if (source.Profile.SchemaVersion != "hephaera-three-vfx-profile/v1") throw new InvalidOperationException("Unsupported effect production profile");
        var profileBytes = JsonFile("production-profile", new ActionFileReference { Path = source.Profile.Path, Sha256 = source.Profile.Sha256 });
        var profile = EffectProfileV2.Parse(profileBytes);
        EffectProfileV2.Validate(profile, inspected.Package, source.EffectId);
        if (profile.NativePackageSha256 != source.Package.Sha256 || profile.PreparationSha256 != source.Preparation?.Sha256)
            throw new InvalidOperationException("Production profile does not bind exact native package/preparation seal");

        var preparationSources = new List<ActionSizedFileReference>();
        VfxPackage? preparedPackage = null;
        if (source.Preparation is not null)
        {
            var prepared = ActionPreparationContextVerifier.Verify(workspace, source, context,
                file => Add(file.Role, file.Path, file.Sha256, file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? MaximumJsonBytes : MaximumFileBytes, file.ResourceId, file.Bytes), cancellation);
            preparationSources.AddRange(prepared.OriginalSources);
            preparedPackage = prepared.PreparedPackage;
            RequireDerivation(inspected.Package, preparedPackage);
        }

        var rightsBytes = JsonFile("rights", source.Rights, "rights");
        var rights = EffectProfileV2.ParseData<ActionEffectRights>(rightsBytes);
        if (rights.SchemaVersion != "bubo-vfx-source-rights/v1" || !rights.Authorized || string.IsNullOrWhiteSpace(rights.License) || string.IsNullOrWhiteSpace(rights.Owner) || rights.Reason.Length < 8 || rights.Inputs is null || rights.Inputs.Count > 1024)
            throw new InvalidOperationException("Effect source requires complete explicit rights assertions");
        var requiredRights = new Dictionary<string, ActionSizedFileReference>(StringComparer.Ordinal);
        foreach (var input in preparationSources) requiredRights[input.Path] = input;
        foreach (var resource in resources)
        {
            var derived = preparedPackage?.Resources.SingleOrDefault(item => item.Id == resource.Id && item.Kind == resource.Kind && item.Sha256 == resource.Sha256 && item.Bytes == resource.Bytes);
            if (derived is null) requiredRights[resource.Path] = new() { Path = resource.Path, Sha256 = resource.Sha256, Bytes = resource.Bytes };
        }
        var rightsPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in rights.Inputs)
        {
            if (!rightsPaths.Add(input.Path) || !requiredRights.TryGetValue(input.Path, out var expected) || expected != input)
                throw new InvalidOperationException("Effect rights input coverage is duplicate, unused or differs from required original bytes");
            if (Path.GetExtension(input.Path).ToLowerInvariant() is not (".png" or ".glb")) throw new InvalidOperationException("Effect rights inputs must be original supported media resources");
            Add("rights-input", input.Path, input.Sha256, MaximumFileBytes, length: input.Bytes, referenceKind: "input");
        }
        if (rightsPaths.Count != requiredRights.Count) throw new InvalidOperationException("An effect resource/preparation source lacks exact rights coverage");

        var productionBytes = JsonFile("production-review", source.ProductionReview, "review");
        var production = EffectProfileV2.ParseData<ActionEffectProductionReview>(productionBytes);
        if (production.SchemaVersion != "bubo-vfx-source-production-review/v1" || production.Decision != (draft ? "pending" : "approved") || !draft && (!production.Independent || string.IsNullOrWhiteSpace(production.Reviewer) || production.Reviewer.Equals(production.Author, StringComparison.OrdinalIgnoreCase)) || string.IsNullOrWhiteSpace(production.Author) || production.Reason.Length < 8 || production.Evidence is null || production.Evidence.Count is < 1 or > 1024)
            throw new InvalidOperationException("Effect source requires current independent production review assertions");
        var evidencePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var evidence in production.Evidence)
        {
            if (!evidencePaths.Add(evidence.Path)) throw new InvalidOperationException("Duplicate private effect production evidence");
            if (Path.GetExtension(evidence.Path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".mp4" or ".webm" or ".json" or ".html" or ".txt" or ".md"))
                throw new InvalidOperationException("Production evidence admits bounded captures/reports, not arbitrary scripts/tools");
            Add("production-evidence", evidence.Path, evidence.Sha256, MaximumFileBytes, length: evidence.Bytes, referenceKind: "review");
        }
        if (source.Correspondences is null || source.Correspondences.Count != resources.Length)
            throw new InvalidOperationException("Every native effect resource requires one canonical correspondence");
        var correspondenceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var measurements = new Dictionary<string, ActionEffectCorrespondenceReport>(StringComparer.Ordinal);
        foreach (var correspondence in source.Correspondences)
        {
            if (!correspondence.Report.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Canonical correspondence must be a JSON file");
            if (!correspondenceIds.Add(correspondence.ResourceId)) throw new InvalidOperationException("Duplicate effect source correspondence");
            var resource = resources.SingleOrDefault(item => item.Id == correspondence.ResourceId) ?? throw new InvalidOperationException("Correspondence names an unknown native effect resource");
            Add("source-correspondence", correspondence.Report.Path, correspondence.Report.Sha256, MaximumJsonBytes, resource.Id, correspondence.Report.Bytes, "input");
            var report = EffectProfileV2.ParseData<ActionEffectCorrespondenceReport>(Read(workspace, correspondence.Report.Path, MaximumJsonBytes, cancellation));
            ValidateCorrespondence(correspondence, resource, report);
            ValidateCorrespondenceContent(workspace, resource, report, cancellation);
            measurements.Add(resource.Id, report);
            if (!production.Evidence.Any(evidence => evidence.Sha256 == report.SourceEvidenceSha256))
                throw new InvalidOperationException("Canonical correspondence lacks its current private production evidence identity");
        }
        var allocations = ValidateResourceMeasurements(profile, inspected.Package, source.EffectId, measurements);
        var subject = SourceFingerprint(files.Values.Where(file => file.Role != "production-review").ToArray(), context, source.EffectId);
        reviewSubject = subject;
        Hash(production.SubjectSha256);
        if (!draft && production.SubjectSha256 != subject) throw new InvalidOperationException("Effect production review subject differs from complete current source closure");
        var inventory = files.Values.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        foreach (var file in inventory)
        {
            var again = Observe(workspace, file.Path, file.Bytes, cancellation);
            if (again.Bytes != file.Bytes || again.Sha256 != file.Sha256) throw new InvalidOperationException("Effect source changed during complete closure inspection");
        }
        workspace.HostContext?.ValidateFresh();
        var sourceInventory = new ActionEffectSourceInventory { Files = inventory, SourceClosureSha256 = SourceFingerprint(inventory, context, source.EffectId) };
        var runtime = new ActionRuntimeEffectSource
        {
            Id = source.Id, EffectId = source.EffectId, Package = source.Package, Profile = source.Profile,
            Resources = resources, Correspondences = source.Correspondences,
            Ancestry = new() { SourceClosureSha256 = sourceInventory.SourceClosureSha256, ProductionReviewSha256 = source.ProductionReview.Sha256, RightsSha256 = source.Rights.Sha256, NativePreparationSha256 = source.Preparation?.Sha256 }
        };
        return new(source, inspected.Package, profile, sourceInventory, runtime) { LayerAllocations = allocations };
    }

    static void RequireDerivation(VfxPackage actual, VfxPackage prepared)
    {
        JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, EffectProfileV2.Json)!;
        if (actual.SchemaVersion != prepared.SchemaVersion || actual.Id != prepared.Id || actual.Version != prepared.Version || actual.StyleSha256 != prepared.StyleSha256 || !JsonNode.DeepEquals(Node(actual.Engine), Node(prepared.Engine)))
            throw new InvalidOperationException("Composed effect changed native preparation identity");
        foreach (var resource in prepared.Resources)
            if (!JsonNode.DeepEquals(Node(resource), Node(actual.Resources.SingleOrDefault(item => item.Id == resource.Id)))) throw new InvalidOperationException("Composed effect changed a prepared resource");
        foreach (var atlas in prepared.Atlases ?? [])
        {
            var current = actual.Atlases?.SingleOrDefault(item => item.ResourceId == atlas.ResourceId) ?? throw new InvalidOperationException("Composed effect lost prepared atlas metadata");
            var originalNode = Node(atlas).AsObject(); var currentNode = Node(current).AsObject();
            var bindings = originalNode["bindings"]!.AsArray(); var currentBindings = currentNode["bindings"]!.AsArray();
            originalNode.Remove("bindings"); currentNode.Remove("bindings");
            if (!JsonNode.DeepEquals(originalNode, currentNode) || bindings.Any(binding => !currentBindings.Any(item => JsonNode.DeepEquals(item, binding))))
                throw new InvalidOperationException("Composed effect changed prepared atlas metadata or original layer bindings");
        }
    }
}
