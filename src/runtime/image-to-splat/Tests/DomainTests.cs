using Bubo.Splats.Domain;

namespace Bubo.Splats.Tests;

internal static class DomainTests
{
    internal static void Run()
    {
        var request = Fixtures.Request();
        SplatRules.ValidateRequest(request);
        Check.That(true, "explicit request admits both initial profiles and a relative source containing spaces");
        SplatRules.ValidateRequest(request with { GaussianCounts = [32_768], Seed = 0 });
        SplatRules.ValidateRequest(request with { GaussianCounts = [131_072], Seed = 9_007_199_254_740_991 });
        Check.That(true, "single density and inclusive safe-integer seed endpoints are accepted");
        Check.Throws<InvalidOperationException>(() => SplatRules.ValidateRequest(null!), "null request is rejected");
        Check.Throws<InvalidOperationException>(() => SplatRules.ValidateSource(null!), "null source is rejected");
        Check.Throws<InvalidOperationException>(() => SplatRules.ValidateBudgets(null!), "null budgets are rejected");

        var invalidRequests = new (string Name, SplatRequest Value)[]
        {
            ("missing schema", request with { SchemaVersion = null! }),
            ("unknown schema", request with { SchemaVersion = "bubo-splat-request/v2" }),
            ("backend fallback", request with { Backend = "gsplat" }),
            ("null backend", request with { Backend = null! }),
            ("null style identity", request with { StyleSha256 = null! }),
            ("null source field", request with { Input = null! }),
            ("null budget field", request with { Budgets = null! }),
            ("null density list", request with { GaussianCounts = null! }),
            ("empty density list", request with { GaussianCounts = [] }),
            ("duplicate density", request with { GaussianCounts = [32_768, 32_768] }),
            ("too many densities", request with { GaussianCounts = [32_768, 131_072, 32_768] }),
            ("unsupported density", request with { GaussianCounts = [65_536] }),
            ("unselected higher density", request with { GaussianCounts = [262_144], Budgets = request.Budgets with { MaxGaussians = 262_144 } }),
            ("negative density", request with { GaussianCounts = [-1] }),
            ("density over caller budget", request with { GaussianCounts = [131_072], Budgets = request.Budgets with { MaxGaussians = 32_768 } }),
            ("negative seed", request with { Seed = -1 }),
            ("unsafe integer seed", request with { Seed = 9_007_199_254_740_992 }),
            ("long overflow-scale seed", request with { Seed = long.MaxValue })
        };
        foreach (var (name, value) in invalidRequests)
            Check.Throws<InvalidOperationException>(() => SplatRules.ValidateRequest(value), name + " is rejected");

        foreach (var hash in new string?[] { null, "", new('A', 64), new('g', 64), new('a', 63), new('a', 65) })
            Check.Throws<InvalidOperationException>(() => SplatRules.ValidateHash(hash!), "invalid lowercase hash identity is rejected");
        SplatRules.ValidateHash(new string('0', 64));
        Check.That(true, "valid complete lowercase hash identity is accepted");

        foreach (var path in new string?[] { null, "", " ", "/absolute.png", "C:/absolute.png", "inputs\\prop.png", "inputs//prop.png",
            "../prop.png", "inputs/../prop.png", "./prop.png", "inputs/./prop.png", "inputs/", "inputs/a:stream.png", "https://host/prop.png",
            "inputs/line\nbreak.png", "inputs/a\0.png", new('a', 513) })
            Check.Throws<InvalidOperationException>(() => SplatRules.ValidateSource(request.Input with { Path = path! }), "invalid source path is rejected");
        SplatRules.ValidateSource(request.Input with { Path = new string('a', 512) });
        Check.That(true, "source path length has an inclusive declared bound");
        Check.Throws<InvalidOperationException>(() => SplatRules.ValidateSource(request.Input with { Sha256 = null! }), "null source hash is rejected");

        var budget = request.Budgets;
        var invalidBudgets = new (string Name, SplatBudgets Value)[]
        {
            ("zero Gaussian count", budget with { MaxGaussians = 0 }),
            ("excessive Gaussian count", budget with { MaxGaussians = 262_145 }),
            ("zero input bytes", budget with { MaxInputBytes = 0 }),
            ("excessive input bytes", budget with { MaxInputBytes = 512L * 1024 * 1024 + 1 }),
            ("zero decoded bytes", budget with { MaxDecodedBytes = 0 }),
            ("excessive decoded bytes", budget with { MaxDecodedBytes = 1024L * 1024 * 1024 + 1 }),
            ("undersized header bound", budget with { MaxHeaderBytes = 127 }),
            ("excessive header bound", budget with { MaxHeaderBytes = 65_537 }),
            ("zero timeout", budget with { TimeoutSeconds = 0 }),
            ("excessive timeout", budget with { TimeoutSeconds = 86_401 }),
            ("undersized log bound", budget with { MaxLogBytes = 1023 }),
            ("excessive log bound", budget with { MaxLogBytes = 16L * 1024 * 1024 + 1 }),
            ("undersized process-memory bound", budget with { MaxProcessMemoryBytes = 64L * 1024 * 1024 - 1 }),
            ("excessive process-memory bound", budget with { MaxProcessMemoryBytes = 128L * 1024 * 1024 * 1024 + 1 })
        };
        foreach (var (name, value) in invalidBudgets)
            Check.Throws<InvalidOperationException>(() => SplatRules.ValidateBudgets(value), name + " is rejected");
        SplatRules.ValidateBudgets(new()
        {
            MaxGaussians = 1, MaxInputBytes = 1, MaxDecodedBytes = 1, MaxHeaderBytes = 128,
            TimeoutSeconds = 1, MaxLogBytes = 1024, MaxProcessMemoryBytes = 64L * 1024 * 1024
        });
        SplatRules.ValidateBudgets(new()
        {
            MaxGaussians = 262_144, MaxInputBytes = 512L * 1024 * 1024, MaxDecodedBytes = 1024L * 1024 * 1024,
            MaxHeaderBytes = 65_536, TimeoutSeconds = 86_400, MaxLogBytes = 16L * 1024 * 1024,
            MaxProcessMemoryBytes = 128L * 1024 * 1024 * 1024
        });
        Check.That(true, "all declared budget endpoint values are accepted independently");
    }
}
