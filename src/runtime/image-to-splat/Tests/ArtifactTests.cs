using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Splats.Domain;

namespace Bubo.Splats.Tests;

internal static class ArtifactTests
{
    internal static void Run()
    {
        var artifact = Artifact();
        SplatRules.ValidateArtifact(artifact);
        Check.That(true, "master artifact explicitly declares uncalibrated source-local geometry and complete ancestry");
        SplatRules.ValidateArtifact(artifact with { GaussianCount = 131_072 });
        SplatRules.ValidateArtifact(artifact with { Ancestry = artifact.Ancestry with { ParentArtifactSha256 = new string('a', 64) } });
        Check.That(true, "both initial artifact densities and an explicit parent identity are supported");
        Check.Throws<InvalidOperationException>(() => SplatRules.ValidateArtifact(null!), "null artifact is rejected");

        var invalid = new (string Name, SplatArtifact Artifact)[]
        {
            ("null artifact schema", artifact with { SchemaVersion = null! }),
            ("unsupported artifact schema", artifact with { SchemaVersion = "bubo-splat-artifact/v2" }),
            ("alternative artifact backend", artifact with { Backend = "gsplat" }),
            ("null artifact backend", artifact with { Backend = null! }),
            ("null artifact style identity", artifact with { StyleSha256 = null! }),
            ("uppercase artifact style identity", artifact with { StyleSha256 = new string('A', 64) }),
            ("null original image identity", artifact with { Input = null! }),
            ("null Gaussian payload identity", artifact with { Payload = null! }),
            ("missing artifact profile", artifact with { Profile = null! }),
            ("undeclared higher-SH artifact profile", artifact with { Profile = "triposplat-ply-sh3/v1" }),
            ("zero artifact count", artifact with { GaussianCount = 0 }),
            ("unsupported artifact count", artifact with { GaussianCount = 65_536 }),
            ("unselected high artifact density", artifact with { GaussianCount = 262_144, Budgets = artifact.Budgets with { MaxGaussians = 262_144 } }),
            ("artifact exceeds count budget", artifact with { Budgets = artifact.Budgets with { MaxGaussians = 32_767 } }),
            ("artifact cannot fit decoded budget", artifact with { Budgets = artifact.Budgets with { MaxDecodedBytes = 32_768 * 68 - 1 } }),
            ("null coordinate declaration", artifact with { Coordinates = null! }),
            ("undeclared target coordinate mapping", artifact with { Coordinates = "world-y-up" }),
            ("null units declaration", artifact with { Units = null! }),
            ("unearned metre calibration", artifact with { Units = "metres" }),
            ("null scale confidence", artifact with { ScaleConfidence = null! }),
            ("unearned verified scale", artifact with { ScaleConfidence = "verified" }),
            ("missing construction ancestry", artifact with { Ancestry = null! }),
            ("missing artifact budgets", artifact with { Budgets = null! }),
            ("artifact input traversal", artifact with { Input = artifact.Input with { Path = "../original.png" } }),
            ("artifact payload traversal", artifact with { Payload = artifact.Payload with { Path = "../asset.ply" } }),
            ("invalid artifact input identity", artifact with { Input = artifact.Input with { Sha256 = "short" } }),
            ("invalid artifact payload identity", artifact with { Payload = artifact.Payload with { Sha256 = "short" } }),
            ("identical original and output paths", artifact with { Payload = artifact.Payload with { Path = artifact.Input.Path } }),
            ("case-aliased original and output paths", artifact with { Payload = artifact.Payload with { Path = artifact.Input.Path.ToUpperInvariant() } })
        };
        foreach (var (name, value) in invalid)
            Check.Throws<InvalidOperationException>(() => SplatRules.ValidateArtifact(value), name + " is rejected");

        foreach (var (name, ancestry) in new (string, SplatAncestry)[]
        {
            ("null request hash", artifact.Ancestry with { RequestSha256 = null! }),
            ("invalid request hash", artifact.Ancestry with { RequestSha256 = new string('g', 64) }),
            ("null source revision", artifact.Ancestry with { SourceRevision = null! }),
            ("short source revision", artifact.Ancestry with { SourceRevision = new string('a', 39) }),
            ("uppercase source revision", artifact.Ancestry with { SourceRevision = new string('A', 40) }),
            ("null model revision", artifact.Ancestry with { ModelRevision = null! }),
            ("invalid model revision", artifact.Ancestry with { ModelRevision = new string('g', 40) }),
            ("long model revision", artifact.Ancestry with { ModelRevision = new string('a', 41) }),
            ("null environment hash", artifact.Ancestry with { EnvironmentSha256 = null! }),
            ("short environment hash", artifact.Ancestry with { EnvironmentSha256 = new string('a', 63) }),
            ("null runtime build hash", artifact.Ancestry with { RuntimeBuildSha256 = null! }),
            ("uppercase runtime build hash", artifact.Ancestry with { RuntimeBuildSha256 = new string('A', 64) }),
            ("null resource catalogue hash", artifact.Ancestry with { ResourceCatalogSha256 = null! }),
            ("invalid resource catalogue hash", artifact.Ancestry with { ResourceCatalogSha256 = new string('z', 64) }),
            ("empty parent artifact identity", artifact.Ancestry with { ParentArtifactSha256 = "" }),
            ("invalid parent artifact identity", artifact.Ancestry with { ParentArtifactSha256 = new string('A', 64) })
        })
            Check.Throws<InvalidOperationException>(() => SplatRules.ValidateArtifact(artifact with { Ancestry = ancestry }), name + " is rejected");

        SplatRules.ValidateArtifact(artifact with { Budgets = artifact.Budgets with { MaxDecodedBytes = 32_768 * 68 } });
        Check.That(true, "artifact decoded-byte bound admits the exact declared SH0 scalar footprint");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(artifact, options);
        var explicitNull = JsonSerializer.Deserialize<SplatArtifact>(json, options)!;
        SplatRules.ValidateArtifact(explicitNull);
        Check.That(explicitNull.Ancestry.ParentArtifactSha256 is null, "required explicit null parent survives serialization");
        var missingParent = JsonNode.Parse(json)!.AsObject();
        missingParent["ancestry"]!.AsObject().Remove("parentArtifactSha256");
        Check.Throws<JsonException>(() => JsonSerializer.Deserialize<SplatArtifact>(missingParent.ToJsonString(), options),
            "required nullable parent field cannot be silently omitted");
    }

    static SplatArtifact Artifact() => new()
    {
        SchemaVersion = "bubo-splat-artifact/v1", Backend = "triposplat", StyleSha256 = new string('a', 64),
        Input = new() { Path = "inputs/original.png", Sha256 = new string('b', 64) },
        Payload = new() { Path = "staging/master.ply", Sha256 = new string('c', 64) },
        Profile = "triposplat-ply-sh0/v1", GaussianCount = 32_768,
        Coordinates = "source-local", Units = "uncalibrated", ScaleConfidence = "unverified",
        Ancestry = new()
        {
            RequestSha256 = new string('d', 64), SourceRevision = "d8db9e018b413dd9c4a9fe22463781bf98e8e68d",
            ModelRevision = "56a96e603204ec410c4da60c13ea4fa09a2169a9", EnvironmentSha256 = new string('e', 64),
            RuntimeBuildSha256 = new string('f', 64), ResourceCatalogSha256 = new string('0', 64), ParentArtifactSha256 = null
        },
        Budgets = Fixtures.Budgets() with { MaxInputBytes = 32L * 1024 * 1024, MaxDecodedBytes = 32L * 1024 * 1024 }
    };
}
