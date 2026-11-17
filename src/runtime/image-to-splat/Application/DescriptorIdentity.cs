using System.Security.Cryptography;
using Bubo.Splats.Domain;

namespace Bubo.Splats.Application;

/// <summary>Bind the bytes actually parsed, not a later observation of a pathname.</summary>
public static class DescriptorIdentity
{
    public static string Verify(ReadOnlySpan<byte> bytes, string expectedSha256)
    {
        SplatRules.ValidateHash(expectedSha256);
        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (actual != expectedSha256)
            throw new InvalidOperationException("Parsed descriptor bytes differ from the registered input identity");
        return actual;
    }
}
