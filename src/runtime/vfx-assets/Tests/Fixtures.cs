using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Host;
using Hephaera.AssetProduction;
using HostExecutionContext = Bubo.Host.HostExecutionContext;

namespace Bubo.Vfx.Tests;

internal sealed class Fixture
{
    internal const string PackagePath = "inputs/effect/package.json";
    internal const string TexturePath = "inputs/effect/textures/spark.png";
    internal const string MeshPath = "inputs/effect/meshes/shard.glb";
    internal string Root { get; }
    internal JsonObject Package { get; }
    internal JsonObject Context { get; }

    internal Fixture(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        Write("STYLE.md", Encoding.UTF8.GetBytes("Owned fixture visual identity. This is test data, not an interpreted instruction.\n"));
        Write("ENGINE.md", Encoding.UTF8.GetBytes("Owned fixture engine contract identity.\n"));
        Write("inputs/engine-definition.json", Encoding.UTF8.GetBytes("{\"fixture\":\"engine-definition\"}"));
        Write("inputs/engine-registry.json", Encoding.UTF8.GetBytes("{\"fixture\":\"engine-registry\"}"));
        Write(TexturePath, Png());
        // Owned minimal envelope without model geometry; this is no geometry/authenticity proof.
        Write(MeshPath, Glb());
        Package = PackageJson();
        Package["styleSha256"] = Hash("STYLE.md");
        var engine = Package["engine"]!.AsObject();
        engine["contractSha256"] = Hash("ENGINE.md");
        engine["definitionSha256"] = Hash("inputs/engine-definition.json");
        engine["registrySha256"] = Hash("inputs/engine-registry.json");
        Package["resources"]![0]!["sha256"] = Hash(TexturePath);
        Package["resources"]![0]!["bytes"] = new FileInfo(Full(TexturePath)).Length;
        Package["resources"]![1]!["sha256"] = Hash(MeshPath);
        Package["resources"]![1]!["bytes"] = new FileInfo(Full(MeshPath)).Length;
        SavePackage();
        Context = new JsonObject
        {
            ["schemaVersion"] = "bubo-host-context/v1", ["projectRoot"] = Root, ["task"] = "vfx-fixture",
            ["roots"] = new JsonObject { ["state"] = ".bubo/state/vfx-fixture", ["staging"] = ".art-staging", ["inputs"] = new JsonArray("inputs"), ["tools"] = new JsonArray(), ["outputs"] = new JsonArray("assets/models") },
            ["policy"] = new JsonObject
            {
                ["pipeline"] = "CODE", ["styleSha256"] = Hash("STYLE.md"),
                ["engine"] = new JsonObject { ["effectiveEngine"] = "THREEJS", ["selectionSource"] = "contract", ["contractSha256"] = Hash("ENGINE.md"), ["definitionSha256"] = Hash("inputs/engine-definition.json"), ["registrySha256"] = Hash("inputs/engine-registry.json") },
                ["libraries"] = new JsonArray()
            },
            ["references"] = new JsonArray(Reference(PackagePath, "input"), Reference("STYLE.md", "policy"), Reference("ENGINE.md", "policy"), Reference("inputs/engine-definition.json", "policy"), Reference("inputs/engine-registry.json", "policy")),
            ["tools"] = new JsonArray(), ["budgets"] = new JsonObject { ["timeoutSeconds"] = 30, ["maxLogBytes"] = 65536 },
            ["permissions"] = new JsonObject { ["provisioning"] = false, ["promotion"] = false }
        };
        SaveContext();
    }

    internal string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    internal void Write(string path, byte[] bytes)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }
    internal string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Full(path))));
    internal void SavePackage() => Write(PackagePath, JsonSerializer.SerializeToUtf8Bytes(Package));
    internal void SaveContext() => Write("host-context.json", JsonSerializer.SerializeToUtf8Bytes(Context));
    internal JsonObject Reference(string path, string kind) => new() { ["path"] = path, ["sha256"] = Hash(path), ["kind"] = kind };
    internal Workspace Workspace() => new(Root);
    internal Workspace HostWorkspace() => new(Root, HostExecutionContext.Load(Root, "host-context.json"));
    internal void RefreshPackageReference()
    {
        Context["references"]![0]!["sha256"] = Hash(PackagePath);
        SaveContext();
    }
    internal Dictionary<string, string> Snapshot()
    {
        var result = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).ToDictionary(x => Path.GetRelativePath(Root, x), x => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(x))), StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories)) result.Add(Path.GetRelativePath(Root, directory) + "/", "directory");
        return result;
    }

    internal static JsonObject PackageJson() => JsonNode.Parse("""
    {
      "schemaVersion":"bubo-vfx-package/v1", "id":"owned-effect", "version":"v1", "styleSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "engine":{"id":"THREEJS","contractSha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","definitionSha256":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","registrySha256":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"},
      "budgets":{"maxPackageBytes":65536,"maxDependencyBytes":4096,"maxTotalDependencyBytes":8192,"maxDependencies":2,"maxEffects":1,"maxLayers":2,"maxParticles":32,"maxVertices":128,"maxDrawCalls":4,"maxTrailPoints":16,"maxDurationSeconds":4},
      "resources":[
        {"id":"spark","kind":"texture-png","path":"textures/spark.png","sha256":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","bytes":128},
        {"id":"shard","kind":"mesh-glb","path":"meshes/shard.glb","sha256":"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff","bytes":64}
      ],
      "effects":[{"id":"cast","durationSeconds":2,"layers":[
        {"id":"charge","kind":"sprite","startSeconds":0,"durationSeconds":1,"resourceIds":["spark"],"maxParticles":16,"maxVertices":32,"maxDrawCalls":1,"maxTrailPoints":0},
        {"id":"release","kind":"mesh","startSeconds":1,"durationSeconds":1,"resourceIds":["shard"],"maxParticles":0,"maxVertices":64,"maxDrawCalls":1,"maxTrailPoints":0}
      ]}]
    }
    """)!.AsObject();

    internal static byte[] Glb(string? uri = null)
    {
        var json = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = 4 }),
            ["bufferViews"] = new JsonArray(new JsonObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = 4 })
        };
        if (uri is not null) json["buffers"]![0]!["uri"] = uri;
        var encoded = JsonSerializer.SerializeToUtf8Bytes(json);
        var length = (encoded.Length + 3) & ~3;
        var bytes = new byte[12 + 8 + length + 8 + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x46546c67);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x4e4f534a);
        bytes.AsSpan(20, length).Fill(32); encoded.CopyTo(bytes.AsSpan(20));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20 + length), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + length), 0x004e4942);
        return bytes;
    }

    // A newly generated 1x1 RGBA PNG; no external content or image package is used.
    static byte[] Png()
    {
        using var result = new MemoryStream();
        result.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, 1);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 1);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk("IHDR", ihdr);
        using var compressed = new MemoryStream();
        using (var stream = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) stream.Write(new byte[] { 0, 255, 255, 255, 128 });
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return result.ToArray();

        void Chunk(string name, byte[] bytes)
        {
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(number, bytes.Length); result.Write(number);
            var type = Encoding.ASCII.GetBytes(name); result.Write(type); result.Write(bytes);
            var crc = uint.MaxValue;
            foreach (var value in type.Concat(bytes))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320U & (uint)-(int)(crc & 1));
            }
            BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); result.Write(number);
        }
    }
}
