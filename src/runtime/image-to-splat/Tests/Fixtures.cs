using System.Security.Cryptography;
using System.Text;
using Bubo.Splats.Domain;

namespace Bubo.Splats.Tests;

internal static class Check
{
    internal static int Passed { get; private set; }

    internal static void That(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        Passed++;
        Console.WriteLine("PASS: " + name);
    }

    internal static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T error) when (!error.Message.StartsWith("FAIL:", StringComparison.Ordinal))
        {
            That(true, name);
            return;
        }
        throw new InvalidOperationException("FAIL: expected " + typeof(T).Name + ": " + name);
    }
}

internal static class Fixtures
{
    internal static readonly string[] Properties = ["x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2",
        "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3"];

    internal static SplatBudgets Budgets() => new()
    {
        MaxGaussians = 131_072, MaxInputBytes = 1024 * 1024, MaxDecodedBytes = 1024 * 1024,
        MaxHeaderBytes = 4096, TimeoutSeconds = 30, MaxLogBytes = 1024 * 1024,
        MaxProcessMemoryBytes = 256L * 1024 * 1024
    };

    internal static SplatRequest Request() => new()
    {
        SchemaVersion = "bubo-splat-request/v1", Backend = "triposplat", StyleSha256 = new string('a', 64),
        Input = new() { Path = "inputs/owned prop.png", Sha256 = new string('b', 64) },
        GaussianCounts = [32_768, 131_072], Seed = 42, Budgets = Budgets()
    };

    internal static float[] Row(float x = 1, float y = 2, float z = 3) =>
        [x, y, z, 0, 0, 0, .5f, -.25f, .125f, 0, -2, -1, 0, 1, 0, 0, 0];

    internal static byte[] Ply(string[]? properties = null, float[][]? rows = null,
        Func<string, string>? rewriteHeader = null, string newline = "\n")
    {
        var names = properties ?? Properties;
        var samples = rows ?? [Row(), Row(-4, 5, -6)];
        var header = "ply\nformat binary_little_endian 1.0\nelement vertex " + samples.Length + "\n"
            + string.Concat(names.Select(name => "property float " + name + "\n")) + "end_header\n";
        if (rewriteHeader is not null) header = rewriteHeader(header);
        if (newline != "\n") header = header.Replace("\n", newline, StringComparison.Ordinal);
        using var bytes = new MemoryStream();
        bytes.Write(Encoding.ASCII.GetBytes(header));
        using (var writer = new BinaryWriter(bytes, Encoding.ASCII, leaveOpen: true))
            foreach (var row in samples)
                foreach (var name in names)
                {
                    var index = Array.IndexOf(Properties, name);
                    writer.Write(index >= 0 ? row[index] : 0f);
                }
        return bytes.ToArray();
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static int HeaderBytes(byte[] bytes)
    {
        var marker = Encoding.ASCII.GetBytes("end_header\n");
        var index = bytes.AsSpan().IndexOf(marker);
        if (index < 0) throw new InvalidOperationException("Fixture has no LF header marker");
        return index + marker.Length;
    }
}
