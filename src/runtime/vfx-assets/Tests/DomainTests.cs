using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Vfx.Domain;

namespace Bubo.Vfx.Tests;

internal static class DomainTests
{
    internal static void Run()
    {
        var good = Parse(Fixture.PackageJson());
        Check.That(VfxValidation.Validate(good).IsValid, "owned typed package has finite budgets and exact resource closure");
        Check.That(good.SchemaVersion == "bubo-vfx-package/v1" && good.Effects[0].Layers.Count == 2, "strict parser preserves versioned effect and layer data");

        JsonObject At(JsonObject value, string location) => location switch
        {
            "package" => value,
            "engine" => value["engine"]!.AsObject(),
            "budgets" => value["budgets"]!.AsObject(),
            "resource" => value["resources"]![0]!.AsObject(),
            "effect" => value["effects"]![0]!.AsObject(),
            "layer" => value["effects"]![0]!["layers"]![0]!.AsObject(),
            _ => throw new InvalidOperationException("Unknown fixture location")
        };
        foreach (var location in new[] { "package", "engine", "budgets", "resource", "effect", "layer" })
        {
            foreach (var property in At(Fixture.PackageJson(), location).Select(x => x.Key).ToArray())
            {
                var missing = Fixture.PackageJson(); At(missing, location).Remove(property);
                RejectParse(missing, location + "." + property + " is required");
                var nil = Fixture.PackageJson(); At(nil, location)[property] = null;
                RejectParse(nil, location + "." + property + " cannot be null");
                var wrongCase = Fixture.PackageJson();
                var target = At(wrongCase, location); var original = target[property]!.DeepClone(); target.Remove(property);
                target[char.ToUpperInvariant(property[0]) + property[1..]] = original;
                RejectParse(wrongCase, location + "." + property + " cannot use wrong-case spelling");
                var raw = Fixture.PackageJson();
                var objectText = At(raw, location).ToJsonString();
                var duplicateText = objectText.Insert(1, JsonSerializer.Serialize(property) + ":" + At(raw, location)[property]!.ToJsonString() + ",");
                var document = raw.ToJsonString().Replace(objectText, duplicateText, StringComparison.Ordinal);
                RejectRaw(document, location + "." + property + " cannot occur twice");
            }
            var unknown = Fixture.PackageJson(); At(unknown, location)["executePython"] = "untrusted.py";
            RejectParse(unknown, location + " rejects executable/unknown recipe fields");
        }
        foreach (var path in new[] { "resources", "effects" })
        {
            var bad = Fixture.PackageJson(); bad[path]!.AsArray().Add(null);
            RejectParse(bad, "null " + path + " element is rejected");
        }
        var nullLayer = Fixture.PackageJson(); nullLayer["effects"]![0]!["layers"]!.AsArray().Add(null);
        RejectParse(nullLayer, "null layer element is rejected");
        var nullReference = Fixture.PackageJson(); nullReference["effects"]![0]!["layers"]![0]!["resourceIds"]!.AsArray().Add(null);
        RejectParse(nullReference, "null resource reference is rejected");
        foreach (var raw in new[] { "null", "[]", "{}", "{", Fixture.PackageJson().ToJsonString() + "{}" }) RejectRaw(raw, "malformed/root-type JSON rejected: " + raw[..Math.Min(12, raw.Length)]);
        RejectRaw(Fixture.PackageJson().ToJsonString().Replace("\"startSeconds\":0", "\"startSeconds\":NaN", StringComparison.Ordinal), "JSON NaN is rejected");
        var overflow = Fixture.PackageJson().ToJsonString().Replace("\"startSeconds\":0", "\"startSeconds\":1e999", StringComparison.Ordinal);
        try { Check.That(!VfxValidation.Validate(VfxJson.Parse(Encoding.UTF8.GetBytes(overflow))).IsValid, "JSON double overflow cannot validate"); }
        catch (InvalidOperationException error) when (!error.Message.StartsWith("FAIL:", StringComparison.Ordinal)) { Check.That(true, "JSON double overflow cannot parse"); }
        var numericType = Fixture.PackageJson(); numericType["budgets"]!["maxParticles"] = "32"; RejectParse(numericType, "numeric string is not accepted as a number");
        var integerOverflow = Fixture.PackageJson(); integerOverflow["budgets"]!["maxParticles"] = (long)int.MaxValue + 1; RejectParse(integerOverflow, "integer overflow is rejected");
        var longOverflow = Fixture.PackageJson(); longOverflow["resources"]![0]!["bytes"] = JsonNode.Parse("9223372036854775808"); RejectParse(longOverflow, "dependency byte-count overflow is rejected");
        var fractional = Fixture.PackageJson(); fractional["budgets"]!["maxEffects"] = 1.5; RejectParse(fractional, "fractional count is rejected");
        RejectRaw(" " + new string(' ', 1024 * 1024) + Fixture.PackageJson().ToJsonString(), "manifest byte hard cap is enforced");

        foreach (var (name, change, code) in new (string, Action<JsonObject>, string)[]
        {
            ("future schema", p => p["schemaVersion"] = "bubo-vfx-package/v2", "invalid-schema"),
            ("unknown engine", p => p["engine"]!["id"] = "UNREGISTERED", "invalid-engine"),
            ("style hash syntax", p => p["styleSha256"] = "bad", "invalid-hash"),
            ("upper-case engine hash", p => p["engine"]!["registrySha256"] = new string('A', 64), "invalid-hash"),
            ("resource kind", p => p["resources"]![0]!["kind"] = "video", "invalid-resource-kind"),
            ("layer kind", p => p["effects"]![0]!["layers"]![0]!["kind"] = "shader-code", "invalid-layer-kind"),
            ("resource zero size", p => p["resources"]![0]!["bytes"] = 0, "invalid-resource-size"),
            ("effect negative duration", p => p["effects"]![0]!["durationSeconds"] = -1, "invalid-duration"),
            ("layer negative start", p => p["effects"]![0]!["layers"]![0]!["startSeconds"] = -1, "invalid-interval"),
            ("layer exceeds effect interval", p => p["effects"]![0]!["layers"]![0]!["startSeconds"] = 2, "invalid-interval"),
            ("layer zero duration", p => p["effects"]![0]!["layers"]![0]!["durationSeconds"] = 0, "invalid-interval"),
            ("zero visible vertices", p => p["effects"]![0]!["layers"]![0]!["maxVertices"] = 0, "invalid-layer-limit"),
            ("zero visible draw calls", p => p["effects"]![0]!["layers"]![0]!["maxDrawCalls"] = 0, "invalid-layer-limit"),
            ("negative layer particle limit", p => p["effects"]![0]!["layers"]![0]!["maxParticles"] = -1, "invalid-layer-limit"),
            ("package ID traversal", p => p["id"] = "../effect", "invalid-id"),
            ("resource ID blank", p => p["resources"]![0]!["id"] = "", "invalid-id"),
            ("resource extension differs from kind", p => p["resources"]![0]!["path"] = "textures/spark.glb", "invalid-resource-kind"),
            ("empty effect inventory", p => p["effects"] = new JsonArray(), "budget-exceeded"),
            ("empty layer inventory", p => p["effects"]![0]!["layers"] = new JsonArray(), "invalid-field"),
            ("missing dependency reference", p => p["effects"]![0]!["layers"]![0]!["resourceIds"] = new JsonArray("missing"), "missing-resource"),
            ("reference case mismatch", p => p["effects"]![0]!["layers"]![0]!["resourceIds"] = new JsonArray("Spark"), "missing-resource"),
            ("repeated reference", p => p["effects"]![0]!["layers"]![0]!["resourceIds"] = new JsonArray("spark", "spark"), "duplicate-resource-reference"),
            ("sprite cannot use mesh", p => p["effects"]![0]!["layers"]![0]!["resourceIds"] = new JsonArray("shard"), "incompatible-resource"),
            ("mesh requires mesh resource", p => p["effects"]![0]!["layers"]![1]!["resourceIds"] = new JsonArray("spark"), "incompatible-resource"),
            ("unreferenced dependency", p => p["effects"]![0]!["layers"]![1]!["resourceIds"] = new JsonArray(), "unreferenced-resource"),
            ("duplicate resource identity", p => p["resources"]![1]!["id"] = "spark", "duplicate-id"),
            ("resource case collision", p => p["resources"]![1]!["id"] = "Spark", "duplicate-id"),
            ("duplicate effect identity", p => p["effects"]!.AsArray().Add(p["effects"]![0]!.DeepClone()), "duplicate-id"),
            ("duplicate layer identity", p => p["effects"]![0]!["layers"]![1]!["id"] = "charge", "duplicate-id"),
            ("resource path case collision", p => p["resources"]![1]!["path"] = "TEXTURES/SPARK.PNG", "duplicate-path"),
            ("dependency count budget", p => p["budgets"]!["maxDependencies"] = 1, "budget-exceeded"),
            ("dependency bytes budget", p => p["budgets"]!["maxDependencyBytes"] = 100, "budget-exceeded"),
            ("dependency sum budget", p => p["budgets"]!["maxTotalDependencyBytes"] = 150, "budget-exceeded"),
            ("global layer count budget", p => p["budgets"]!["maxLayers"] = 1, "budget-exceeded"),
            ("global particle budget", p => p["budgets"]!["maxParticles"] = 8, "budget-exceeded"),
            ("global vertex budget", p => p["budgets"]!["maxVertices"] = 32, "budget-exceeded"),
            ("global draw-call budget", p => p["budgets"]!["maxDrawCalls"] = 1, "budget-exceeded"),
            ("duration budget", p => p["budgets"]!["maxDurationSeconds"] = 1, "budget-exceeded")
        }) Invalid(change, name, code);

        foreach (var path in new[] { "../outside.png", "/absolute.png", "C:/absolute.png", "textures\\spark.png", "textures/./spark.png", "textures//spark.png", ".git/config.png", "textures/spark.png.", "textures/NUL.png", "textures/spark.png:stream", "https://host/spark.png" })
            Invalid(p => p["resources"]![0]!["path"] = path, "unsafe dependency path " + path, "invalid-path");
        foreach (var field in Fixture.PackageJson()["budgets"]!.AsObject().Select(x => x.Key))
            Invalid(p => p["budgets"]![field] = -1, "negative budget " + field, "invalid-budget");
        foreach (var (field, limit) in new (string, long)[]
        {
            ("maxPackageBytes", VfxJson.MaximumPackageBytes), ("maxDependencyBytes", VfxLimits.MaximumDependencyBytes),
            ("maxTotalDependencyBytes", VfxLimits.MaximumTotalDependencyBytes), ("maxDependencies", VfxLimits.MaximumDependencies),
            ("maxEffects", VfxLimits.MaximumEffects), ("maxLayers", VfxLimits.MaximumLayers),
            ("maxParticles", VfxLimits.MaximumParticles), ("maxVertices", VfxLimits.MaximumVertices),
            ("maxDrawCalls", VfxLimits.MaximumDrawCalls), ("maxTrailPoints", VfxLimits.MaximumTrailPoints)
        }) Invalid(p => p["budgets"]![field] = limit + 1, "absolute admission ceiling " + field, "invalid-budget");
        Invalid(p => p["budgets"]!["maxDurationSeconds"] = VfxLimits.MaximumDurationSeconds + 1, "absolute duration admission ceiling", "invalid-budget");
        foreach (var limit in new[] { "maxParticles", "maxVertices", "maxDrawCalls", "maxTrailPoints" })
            Invalid(p => p["effects"]![0]!["layers"]![0]![limit] = -1, "negative layer limit " + limit, "invalid-layer-limit");
        Check.That(!VfxValidation.Validate(good with { Budgets = good.Budgets with { MaxDurationSeconds = double.NaN } }).IsValid, "typed NaN budget is rejected");
        Check.That(!VfxValidation.Validate(good with { Effects = [good.Effects[0] with { DurationSeconds = double.PositiveInfinity }] }).IsValid, "typed infinite duration is rejected");
        Check.That(!VfxValidation.Validate(good with { Effects = [good.Effects[0] with { Layers = [good.Effects[0].Layers[0] with { StartSeconds = double.NaN }] }] }).IsValid, "typed NaN interval is rejected");
        Check.That(!VfxValidation.Validate(good with { Effects = [good.Effects[0] with { Layers = [good.Effects[0].Layers[0] with { StartSeconds = double.MaxValue, DurationSeconds = double.MaxValue }] }] }).IsValid, "typed finite values with overflowing interval sum are rejected");
        Invalid(p =>
        {
            var second = p["effects"]![0]!.DeepClone(); second["id"] = "second"; p["effects"]!.AsArray().Add(second);
            p["budgets"]!["maxEffects"] = 2; p["budgets"]!["maxLayers"] = 4; p["budgets"]!["maxParticles"] = 64;
            p["budgets"]!["maxVertices"] = 256; p["budgets"]!["maxDrawCalls"] = 8; p["budgets"]!["maxDurationSeconds"] = 3;
        }, "aggregate effect duration exceeds bound even when each effect fits", "budget-exceeded");
        Invalid(p => p["effects"]![0]!["layers"]![0]!["maxTrailPoints"] = 17, "global trail-point budget", "budget-exceeded");
        foreach (var kind in new[] { "ring", "burst", "projectile", "trail", "impact", "ground-indicator", "beam" })
        {
            var candidate = Fixture.PackageJson(); candidate["effects"]![0]!["layers"]![0]!["kind"] = kind;
            Check.That(VfxValidation.Validate(Parse(candidate)).IsValid, "declared " + kind + " data validates without asserting rendering support");
        }
    }

    internal static VfxPackage Parse(JsonObject value) => VfxJson.Parse(JsonSerializer.SerializeToUtf8Bytes(value));
    static void RejectParse(JsonObject value, string name) => Check.Throws<InvalidOperationException>(() => Parse(value), name);
    static void RejectRaw(string value, string name) => Check.Throws<InvalidOperationException>(() => VfxJson.Parse(Encoding.UTF8.GetBytes(value)), name);
    static void Invalid(Action<JsonObject> mutate, string name, string code)
    {
        var value = Fixture.PackageJson(); mutate(value);
        var result = VfxValidation.Validate(Parse(value));
        Check.That(!result.IsValid && result.Issues.Any(x => x.Code == code && x.Path.Length != 0 && x.Message.Length != 0), name + " reports " + code);
    }
}
