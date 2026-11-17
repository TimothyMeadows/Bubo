using System.Text.Json;
using Bubo.Splats.Application;
using Bubo.Splats.Domain;

namespace Bubo.Splats.Tests;

internal static class DescriptorIdentityTests
{
    internal static void Run()
    {
        var request = Fixtures.Request();
        var replacement = request with { Seed = 43 };
        SplatRules.ValidateRequest(request);
        SplatRules.ValidateRequest(replacement);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var original = JsonSerializer.SerializeToUtf8Bytes(request, options);
        var swapped = JsonSerializer.SerializeToUtf8Bytes(replacement, options);
        var expected = Fixtures.Hash(original);
        Check.That(original.Length == swapped.Length && !original.SequenceEqual(swapped),
            "distinct valid descriptor buffers can have identical lengths");
        Check.That(DescriptorIdentity.Verify(original, expected) == expected,
            "registered identity accepts the exact descriptor buffer");
        // Restoring the pathname later cannot change the bytes that the parser actually received.
        Check.Throws<InvalidOperationException>(() => DescriptorIdentity.Verify(swapped, expected),
            "same-size swapped descriptor buffer cannot inherit the original identity");
        foreach (var invalid in new string?[] { null, "not-a-sha256", expected.ToUpperInvariant() })
            Check.Throws<InvalidOperationException>(() => DescriptorIdentity.Verify(original, invalid!),
                "descriptor buffer verification rejects a malformed expected identity");
    }
}
