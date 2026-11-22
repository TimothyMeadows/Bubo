using System.Text.Json.Nodes;
using Hephaera.ProofToModel.Domain;

namespace Hephaera.ProofToModel.Application;

public sealed partial class Pipeline
{
    void ValidateConditioningProfile()
    {
        MultiViewGeometry.ValidateManifestProfile(m);
        if (!IsFourView) return;
        var full = w.Resolve(m.ConditioningProfileFile!);
        if (!m.ConditioningProfileFile!.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !File.Exists(full) || new FileInfo(full).Length is < 2 or > 65536)
            throw new InvalidOperationException("Conditioning profile must be a bounded existing JSON file (64 KiB)");
        var profile = w.Load<JsonObject>(m.ConditioningProfileFile!);
        string[] keys = ["schemaVersion", "id", "mode", "modelFamily", "viewCount", "views", "baseToolchain"];
        if (profile.Count != keys.Length || profile.Any(pair => !keys.Contains(pair.Key, StringComparer.Ordinal)) ||
            profile["schemaVersion"]?.GetValue<int>() != 1 || profile["id"]?.GetValue<string>() != MultiViewGeometry.CardinalFourProfile ||
            profile["mode"]?.GetValue<string>() != "multi-view-four" || profile["modelFamily"]?.GetValue<string>() != "mv" ||
            profile["viewCount"]?.GetValue<int>() != 4 || profile["views"] is not JsonArray views ||
            !views.Select(view => view!.GetValue<string>()).SequenceEqual(ConditioningViews, StringComparer.Ordinal))
            throw new InvalidOperationException("Exact cardinal-four-v1 profile required");
        var source = profile["baseToolchain"] as JsonObject ?? throw new InvalidOperationException("Four-view profile needs its unchanged base toolchain reference");
        if (source.Count != 2 || !source.ContainsKey("path") || !source.ContainsKey("sha256") ||
            source["path"]?.GetValue<string>() != m.Toolchain || source["sha256"]?.GetValue<string>() != Hash(m.Toolchain))
            throw new InvalidOperationException("Four-view profile must bind the unchanged selected base toolchain bytes");
        RequireMultiViewToolchain();
        ValidateFourViewBaseArguments();
    }

    void ValidateFourViewBaseArguments()
    {
        // The accepted base toolchain and installation remain six-view. This is the
        // only permitted argument overlay: one explicit count substituted after validation.
        string[] expected = ["--views", "{input}", "--models", "{modelDir}", "--pixal3d-weights", "mv", "--num-views", "6",
            "--res", "1024", "--seed", "42", "--gss", "10", "--require-gpu", "{output}"];
        if (Section("reconstruction")["arguments"] is not JsonArray arguments ||
            !arguments.Select(value => value!.GetValue<string>()).SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidOperationException("Four-view overlay requires the exact six-view MV base argument template; no alternate flags or fallback");
    }
}
