using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bubo.Vfx.Application;
using Bubo.Vfx.Domain;
using Bubo.Vfx.Infrastructure;
using Hephaera.AssetProduction;
using Owned = Bubo.Vfx.Tests.PreparationFixtures.Owned;

namespace Bubo.Vfx.Tests;

internal static class PreparationTests
{
    static readonly string Build = new('a', 64);
    const string Output = ".art-staging/prepared/v1";

    internal static void Run(string root, string? cli = null)
    {
        var own = Path.Combine(root, "preparation");
        Directory.CreateDirectory(own);
        Check.That(PreparationFixtures.Pattern(2, 3).Where((_, i) => i % 4 == 3).Distinct().Order().SequenceEqual(new byte[] { 0, 63, 173, 255 }),
            "owned alpha fixture includes transparent, low, high and opaque samples independently of switch-expression precedence");
        Domain(own);
        Codec(own);
        Pixels(own);
        Lifecycle(own);
        ContrastDiagnostics(own);
        if (cli is not null) Cli(cli, own);
        else Check.Limits.Add("Preparation public CLI/interruption checks need --cli EXISTING_BUBO_DLL_OR_EXE.");
    }

    static VfxPreparationRequest Parse(Owned fixture) => PreparationJson.Parse(JsonSerializer.SerializeToUtf8Bytes(fixture.Request));
    static JsonObject Data(object value) => PreparationFixtures.Object(value);
    static void Reject(Action action, string name)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException or NotSupportedException)
        { Check.That(true, name); return; }
        throw new InvalidOperationException("FAIL: expected preparation rejection: " + name);
    }

    static void Domain(string root)
    {
        foreach (var profile in new[] { "sequence", "sheet", "math-mask" })
        {
            var fixture = new Owned(Path.Combine(root, "domain-good-" + profile), profile);
            var request = Parse(fixture);
            Check.That(PreparationValidation.Validate(request).IsValid, "strict preparation admits owned " + profile);
            Check.That(PreparationValidation.Validate(PreparationJson.Parse(PreparationJson.Serialize(request))).IsValid, "preparation data round-trips without inferred defaults: " + profile);
        }
        var baseline = new Owned(Path.Combine(root, "domain-negative"));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(baseline.Request);
        foreach (var (name, mutate) in new (string, Action<JsonObject>)[]
        {
            ("unknown top-level field", x => x["callback"] = "untrusted()"),
            ("unknown nested field", x => x["timing"]!["sort"] = "lexical"),
            ("null property", x => x["sources"]![0]!["path"] = null),
            ("wrong casing", x => { x["Profile"] = x["profile"]!.DeepClone(); x.Remove("profile"); }),
            ("missing required", x => x.Remove("timing"))
        })
        {
            var bad = baseline.Request.DeepClone().AsObject(); mutate(bad);
            Reject(() => PreparationJson.Parse(JsonSerializer.SerializeToUtf8Bytes(bad)), "strict JSON rejects " + name);
        }
        var duplicate = Encoding.UTF8.GetString(bytes).Replace("\"profile\":\"sequence\"", "\"profile\":\"sequence\",\"profile\":\"sheet\"", StringComparison.Ordinal);
        Reject(() => PreparationJson.Parse(Encoding.UTF8.GetBytes(duplicate)), "duplicate JSON keys never choose a processing profile");
        foreach (var (name, mutate) in new (string, Action<JsonObject>)[]
        {
            ("video is unavailable", x => x["profile"] = "video"),
            ("premultiplied conversion unavailable", x => x["atlas"]!["alpha"] = "premultiplied"),
            ("unsupported color conversion", x => x["atlas"]!["colorSpace"] = "display-p3"),
            ("zero rate", x => x["timing"]!["rateNumerator"] = 0),
            ("negative rate denominator", x => x["timing"]!["rateDenominator"] = -1),
            ("zero frame step", x => x["timing"]!["frameStep"] = 0),
            ("out-of-range span", x => x["timing"]!["firstFrame"] = 3),
            ("arithmetic span overflow", x => { x["timing"]!["firstFrame"] = int.MaxValue; x["timing"]!["frameStep"] = int.MaxValue; }),
            ("frame cap", x => x["budgets"]!["maxFrames"] = 1),
            ("encoded source cap", x => x["budgets"]!["maxSourceBytes"] = 1),
            ("total source cap", x => x["budgets"]!["maxTotalSourceBytes"] = 1),
            ("path traversal", x => x["sources"]![0]!["path"] = "../frame.png"),
            ("path wildcard", x => x["sources"]![0]!["path"] = "inputs/*.png"),
            ("path backslash", x => x["sources"]![0]!["path"] = "inputs\\frame.png"),
            ("case-alias source", x => x["sources"]![1]!["path"] = x["sources"]![0]!["path"]!.GetValue<string>().ToUpperInvariant()),
            ("incompatible sheet", x => x["sheet"] = new JsonObject()),
            ("unbounded output", x => x["budgets"]!["maxOutputBytes"] = 0),
            ("unbounded work", x => x["budgets"]!["maxPixelWork"] = 0),
            ("unbounded allocation", x => x["budgets"]!["maxWorkingBytes"] = 0),
            ("unbounded time", x => x["budgets"]!["maxSeconds"] = 0)
        })
        {
            var bad = baseline.Request.DeepClone().AsObject(); mutate(bad);
            var refused = false;
            try { refused = !PreparationValidation.Validate(PreparationJson.Parse(JsonSerializer.SerializeToUtf8Bytes(bad))).IsValid; }
            catch (InvalidOperationException) { refused = true; }
            Check.That(refused, "preparation rejects " + name);
        }
        var mask = Parse(new Owned(Path.Combine(root, "domain-finite-mask"), "math-mask"));
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Check.That(!PreparationValidation.Validate(mask with { Mask = mask.Mask! with { Softness = value } }).IsValid, "direct typed mask rejects nonfinite parameters: " + value);
    }

    static void Codec(string root)
    {
        var pixels = PreparationFixtures.Pattern(5, 4);
        foreach (var gray in new[] { false, true })
        foreach (var filter in Enumerable.Range(0, 5))
        {
            using var input = new MemoryStream(PreparationFixtures.Png(5, 4, pixels, filter, gray));
            var decoded = RgbaPngCodec.Read(input, new() { ColorSpace = "srgb" });
            Check.That(decoded.Width == 5 && decoded.Height == 4 && decoded.Pixels.SequenceEqual(gray ? PreparationFixtures.GrayPixels(pixels) : pixels), "independent PNG filter " + filter + (gray ? " gray-alpha" : " RGBA") + " preserves every channel including transparent RGB");
        }
        foreach (var (profile, width, height, expectedBlock) in new[] { ("stored", 5, 4, 0), ("fixed", 5, 4, 1), ("default", 64, 64, 2) })
        {
            var expected = PreparationFixtures.Pattern(width, height);
            var encoded = PreparationFixtures.Png(width, height, expected, deflateProfile: profile);
            Check.That(PreparationFixtures.FirstDeflateBlockType(encoded) == expectedBlock, "owned valid PNG fixture contains actual DEFLATE block family " + expectedBlock);
            var decoded = RgbaPngCodec.Read(new MemoryStream(encoded), new() { ColorSpace = "srgb" });
            Check.That(decoded.Pixels.SequenceEqual(expected), "strict token walker preserves exact pixels through DEFLATE block family " + expectedBlock);
        }
        var original = PreparationFixtures.Png(5, 4, pixels);
        foreach (var (name, encoded) in new (string, byte[])[]
        {
            ("CRC corruption", CorruptCrc(original)),
            ("truncated IEND", original[..^3]),
            ("trailing bytes", original.Concat(new byte[] { 0 }).ToArray()),
            ("extra inflated scanline data", PreparationFixtures.Png(5, 4, pixels, extraInflatedByte: true)),
            ("invalid scanline filter", PreparationFixtures.Png(5, 4, pixels, invalidFilter: true)),
            ("unsupported critical chunk", PreparationFixtures.Png(5, 4, pixels, ancillary: [("ABCD", new byte[] { 0 })])),
            ("ICC profile conversion", PreparationFixtures.Png(5, 4, pixels, ancillary: [("iCCP", new byte[] { 0 })])),
            ("animated PNG", PreparationFixtures.Png(5, 4, pixels, ancillary: [("acTL", new byte[8])])),
            ("conflicting linear gamma", PreparationFixtures.Png(5, 4, pixels, ancillary: [("gAMA", Gamma(100000))])),
            ("sRGB after palette", PreparationFixtures.Png(5, 4, pixels, ancillary: [("PLTE", new byte[] { 0, 0, 0 }), ("sRGB", new byte[] { 0 })])),
            ("gamma after palette", PreparationFixtures.Png(5, 4, pixels, ancillary: [("PLTE", new byte[] { 0, 0, 0 }), ("gAMA", Gamma(45455))]))
        })
        {
            Reject(() => RgbaPngCodec.Read(new MemoryStream(encoded), new() { ColorSpace = "srgb" }), "bounded strict codec rejects " + name);
        }
        foreach (var (space, chunks) in new (string, IReadOnlyList<(string, byte[])>)[]
        {
            ("srgb", [("sRGB", new byte[] { 0 }), ("gAMA", Gamma(45455))]),
            ("linear", [("gAMA", Gamma(100000))])
        })
        {
            var decoded = RgbaPngCodec.Read(new MemoryStream(PreparationFixtures.Png(5, 4, pixels, ancillary: chunks)), new() { ColorSpace = space });
            Check.That(decoded.Pixels.SequenceEqual(pixels), "explicit " + space + " profile accepts bytes without conversion");
        }
        foreach (var (name, tail) in new (string, byte[])[]
        {
            ("garbage", new byte[] { 3, 9, 27, 81 }),
            ("second-zlib", new byte[] { 0x78, 0x9c, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 })
        })
        {
            Reject(() => RgbaPngCodec.Read(new MemoryStream(PreparationFixtures.Png(5, 4, pixels, compressedTail: tail)), new() { ColorSpace = "srgb" }), "strict codec rejects CRC-valid IDAT " + name + " after its sole zlib stream");
        }
        foreach (var remove in Enumerable.Range(1, 8))
            Reject(() => RgbaPngCodec.Read(new MemoryStream(PreparationFixtures.Png(5, 4, pixels, truncateCompressedBytes: remove)), new() { ColorSpace = "srgb" }), "strict codec rejects truncated zlib footer/deflate stream: " + remove + " missing bytes with valid PNG CRC");
        Reject(() => RgbaPngCodec.Read(new MemoryStream(PreparationFixtures.Png(5, 4, pixels, ancillary: Enumerable.Repeat(("IDAT", Array.Empty<byte>()), 4096).ToArray())), new() { ColorSpace = "srgb" }), "strict codec bounds actual PNG chunk count independently of tiny chunk bytes");
        foreach (var (name, limits) in new (string, RgbaPngCodec.Limits)[]
        {
            ("encoded", new() { MaxEncodedBytes = original.Length - 1 }),
            ("chunk", new() { MaxChunkBytes = 12 }),
            ("compressed", new() { MaxCompressedBytes = 1 }),
            ("decoded pixels", new() { MaxPixels = 19 }),
            ("working allocation", new() { MaxWorkingBytes = 16 })
        }) Reject(() => RgbaPngCodec.Read(new MemoryStream(original), limits), "codec enforces " + name + " cap before excess work");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Check.Throws<OperationCanceledException>(() => RgbaPngCodec.Read(new MemoryStream(original), cancellation: canceled.Token), "codec read honors cancellation");
        Check.Throws<OperationCanceledException>(() => RgbaPngCodec.Write(new MemoryStream(), new(5, 4, pixels), cancellation: canceled.Token), "codec write honors cancellation");
        using var duringRead = new CancellationTokenSource();
        using var cancelingInput = new CancelingReadStream(original, duringRead);
        Check.Throws<OperationCanceledException>(() => RgbaPngCodec.Read(cancelingInput, cancellation: duringRead.Token), "codec cancellation after actual input reads stops without decoding success");
        Reject(() => RgbaPngCodec.Write(new MemoryStream(), new(5, 4, pixels), new() { MaxOutputBytes = 1 }), "codec output cannot exceed declared encoded limit");
        var output = Path.Combine(root, "codec-owned-exclusive.png");
        RgbaPngCodec.Write(output, new(5, 4, pixels));
        var before = File.ReadAllBytes(output);
        Reject(() => RgbaPngCodec.Write(output, new(5, 4, pixels)), "codec path output never overwrites a previous file");
        Check.That(File.ReadAllBytes(output).SequenceEqual(before), "exclusive-write failure preserves exact output bytes");
    }

    static byte[] CorruptCrc(byte[] png) { var result = (byte[])png.Clone(); result[29] ^= 1; return result; }
    static byte[] Gamma(int value) { var bytes = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); return bytes; }

    static void Pixels(string root)
    {
        foreach (var profile in new[] { "sequence", "sheet" })
        {
            var fixture = new Owned(Path.Combine(root, "pixels-" + profile), profile);
            var rendered = VfxPixelPreparation.Render(Parse(fixture), (source, limits, cancellation) => RgbaPngCodec.Read(fixture.Full(source.Path), limits, cancellation));
            var metadata = Data(rendered)["atlas"]!.AsObject();
            var frames = metadata["frames"]!.AsArray();
            Check.That(frames.Count == 3, profile + " preserves exact declared frame count");
            Check.That(metadata["origin"]!.GetValue<string>() == "top-left" && metadata["rateNumerator"]!.GetValue<int>() == 24
                && metadata["rateDenominator"]!.GetValue<int>() == 1 && metadata["durationNumerator"]!.GetValue<long>() == 1
                && metadata["durationDenominator"]!.GetValue<int>() == 8 && metadata["loop"]!.GetValue<string>() == "repeat",
                profile + " records exact rational timing, reduced duration and declared repeat intent");
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i]!;
                var x = frame["x"]!.GetValue<int>(); var y = frame["y"]!.GetValue<int>();
                var expected = fixture.Frames[profile == "sequence" ? i : new[] { 2, 0, 1 }[i]];
                var actual = Extract(rendered.Image.Pixels, rendered.Image.Width, x, y, 2, 3);
                Check.That(actual.SequenceEqual(expected), profile + " explicit frame " + i + " has correct origin/order/straight alpha/color");
                var left = frame["cellX"]!.GetValue<int>(); var top = frame["cellY"]!.GetValue<int>();
                var width = frame["cellWidth"]!.GetValue<int>(); var height = frame["cellHeight"]!.GetValue<int>();
                Check.That(Pixel(rendered.Image, left, top).SequenceEqual(new byte[4]) && Pixel(rendered.Image, left + width - 1, top + height - 1).SequenceEqual(new byte[4]), profile + " frame " + i + " retains transparent outer padding");
                Check.That(Pixel(rendered.Image, x - 1, y - 1).SequenceEqual(expected[..4]) && Pixel(rendered.Image, x + 2, y + 3).SequenceEqual(expected[^4..]), profile + " frame " + i + " extrudes the exact corner colors and alpha");
            }
            var repeat = VfxPixelPreparation.Render(Parse(fixture), (source, limits, cancellation) => RgbaPngCodec.Read(fixture.Full(source.Path), limits, cancellation));
            Check.That(rendered.Image.Pixels.SequenceEqual(repeat.Image.Pixels) && JsonSerializer.Serialize(rendered.Atlas) == JsonSerializer.Serialize(repeat.Atlas), profile + " deterministic fixed-input atlas pixels and metadata");
        }
        foreach (var kind in new[] { "disc", "ring", "ramp" })
        {
            var fixture = new Owned(Path.Combine(root, "pixels-mask-" + kind), "math-mask");
            fixture.Request["mask"]!["kind"] = kind;
            if (kind == "ring") fixture.Request["mask"]!["innerRadius"] = 0.4;
            fixture.Save();
            var rendered = VfxPixelPreparation.Render(Parse(fixture), (_, _, _) => throw new InvalidOperationException("Mask must read no source"));
            Check.That(rendered.Image.Width == 13 && rendered.Image.Height == 11, kind + " mask fits explicit cell and margins");
            var alpha = rendered.Image.Pixels.Where((_, index) => index % 4 == 3).ToArray();
            Check.That(alpha.Any(a => a > 0) && alpha.Any(a => a == 0), kind + " produces nonempty separate alpha with transparent exterior");
            Check.That(kind != "ring" || Pixel(rendered.Image, 6, 5)[3] == 0, "ring retains a real transparent center");
        }
        var span = new Owned(Path.Combine(root, "pixels-selected-span"));
        span.Request["timing"]!["frameCount"] = 2; span.Request["timing"]!["frameStep"] = 2;
        span.Request["timing"]!["rateNumerator"] = 30000; span.Request["timing"]!["rateDenominator"] = 1001;
        span.Save();
        var readOrder = new List<string>();
        var selected = VfxPixelPreparation.Render(Parse(span), (source, limits, token) => { readOrder.Add(source.Path); return RgbaPngCodec.Read(span.Full(source.Path), limits, token); });
        Check.That(readOrder.SequenceEqual(new[] { "inputs/preparation/frame-0.png", "inputs/preparation/frame-2.png" })
            && selected.Atlas.Frames.Select(f => f.SourceFrame).SequenceEqual(new[] { 0, 2 }), "selected span honors exact declared step and ordered source identities");
        Check.That(selected.Atlas.DurationNumerator == 1001 && selected.Atlas.DurationDenominator == 15000,
            "nonintegral rational frame rate preserves exact selected duration without rounding");
        span.Request["timing"]!["firstFrame"] = 1; span.Request["timing"]!["frameCount"] = 1;
        span.Request["timing"]!["frameStep"] = 1; span.Request["atlas"]!["columns"] = 1; span.Save();
        selected = VfxPixelPreparation.Render(Parse(span), (source, limits, token) => RgbaPngCodec.Read(span.Full(source.Path), limits, token));
        var only = selected.Atlas.Frames[0];
        Check.That(only.SourceFrame == 1 && Extract(selected.Image.Pixels, selected.Image.Width, only.X, only.Y, 2, 3).SequenceEqual(span.Frames[1]), "nonzero first-frame selection neither sorts nor silently includes earlier frames");
        foreach (var (name, field, value) in new[]
        {
            ("decoded pixels", "maxDecodedPixels", 1L), ("atlas pixels", "maxAtlasPixels", 1L),
            ("pixel work", "maxPixelWork", 1L), ("working bytes", "maxWorkingBytes", 1L)
        })
        {
            var fixture = new Owned(Path.Combine(root, "pixels-bound-" + field)); fixture.Request["budgets"]![field] = value;
            Reject(() => VfxPixelPreparation.Render(Parse(fixture), (source, limits, cancellation) => RgbaPngCodec.Read(fixture.Full(source.Path), limits, cancellation)), "render refuses declared " + name + " overflow");
        }
        var canceledFixture = new Owned(Path.Combine(root, "pixels-canceled"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Check.Throws<OperationCanceledException>(() => VfxPixelPreparation.Render(Parse(canceledFixture), (source, limits, token) => RgbaPngCodec.Read(canceledFixture.Full(source.Path), limits, token), cancellation.Token), "pixel preparation honors cancellation before work");
        var timed = new Owned(Path.Combine(root, "pixels-actual-timeout")); timed.Request["budgets"]!["maxSeconds"] = 1; timed.Save();
        var expired = false;
        try
        {
            VfxPixelPreparation.Render(Parse(timed), (source, limits, token) =>
            {
                Thread.Sleep(1100);
                return RgbaPngCodec.Read(timed.Full(source.Path), limits, token);
            });
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException) { expired = true; }
        Check.That(expired, "actual elapsed processing deadline stops a delayed source callback");
    }

    static byte[] Pixel(RgbaPngCodec.Image image, int x, int y) => image.Pixels.AsSpan((y * image.Width + x) * 4, 4).ToArray();
    static byte[] Extract(byte[] pixels, int canvasWidth, int left, int top, int width, int height)
    {
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++) pixels.AsSpan(((top + y) * canvasWidth + left) * 4, width * 4).CopyTo(result.AsSpan(y * width * 4));
        return result;
    }

    static void Lifecycle(string root)
    {
        var good = new Owned(Path.Combine(root, "lifecycle-good"));
        var result = VfxPreparation.Prepare(good.Workspace(), Owned.RequestPath, Output, Build);
        var inspected = VfxInspection.Inspect(good.Workspace(), result.PackagePath);
        Check.That(inspected.IsValid && inspected.Package.SchemaVersion == "bubo-vfx-package/v2" && inspected.ObservedResources.Count == 1, "prepared portable package passes independent native closure inspection");
        Check.That(VfxPreparation.Verify(good.Workspace(), result.ManifestPath, Build).PackageSha256 == result.PackageSha256, "reopened immutable preparation verifies actual payload and source ancestry");
        var before = good.Host.Snapshot();
        Reject(() => VfxPreparation.Prepare(good.Workspace(), Owned.RequestPath, Output, Build), "same output attempt cannot overwrite immutable preparation");
        Check.That(InspectionTests.Equal(before, good.Host.Snapshot()), "attempt collision preserves exact existing preparation");
        Reject(() => VfxPreparation.Prepare(good.Workspace(), Owned.RequestPath, ".art-staging/redirected-same-version", Build), "a different output directory cannot reset the same asset/version reservation");
        Check.That(InspectionTests.Equal(before, good.Host.Snapshot()), "redirected duplicate version preserves all source, state and payload bytes");
        Reject(() => VfxPreparation.Verify(good.Workspace(), result.ManifestPath, new string('b', 64)), "preparation cannot inherit verification under a different native build");
        Check.That(!result.HumanAcceptance && !result.PromotionAvailable && result.LoopQuality == "not-established-by-pixel-or-endpoint-checks", "technical preparation grants neither seamless-loop quality nor human/promotion acceptance");
        var inputPixelCap = new Owned(Path.Combine(root, "lifecycle-separate-input-atlas-caps"));
        inputPixelCap.Request["budgets"]!["maxDecodedPixels"] = 6; inputPixelCap.Save();
        var capped = VfxPreparation.Prepare(inputPixelCap.Workspace(), Owned.RequestPath, Output, Build);
        Check.That(VfxPreparation.Verify(inputPixelCap.Workspace(), capped.ManifestPath, Build).IsValid, "atlas reopening uses its own pixel bound instead of the smaller per-source decoded bound");
        foreach (var (name, mutate) in new (string, Action<JsonObject>)[]
        {
            ("actual texture dimension", p => p["atlases"]![0]!["width"] = p["atlases"]![0]!["width"]!.GetValue<int>() + 1),
            ("overlapping frame cells", p => { p["atlases"]![0]!["frames"]![1]!["cellX"] = 0; p["atlases"]![0]!["frames"]![1]!["x"] = 2; }),
            ("rational duration", p => p["atlases"]![0]!["durationNumerator"] = 99),
            ("resource binding", p => p["atlases"]![0]!["bindings"]![0]!["layerId"] = "absent")
        })
        {
            var fixture = new Owned(Path.Combine(root, "lifecycle-v2-" + name.Replace(' ', '-')));
            var prepared = VfxPreparation.Prepare(fixture.Workspace(), Owned.RequestPath, Output, Build);
            var package = JsonNode.Parse(File.ReadAllBytes(fixture.Full(prepared.PackagePath)))!.AsObject(); mutate(package);
            File.WriteAllBytes(fixture.Full(prepared.PackagePath), JsonSerializer.SerializeToUtf8Bytes(package));
            var rejected = false;
            try { rejected = !VfxInspection.Inspect(fixture.Workspace(), prepared.PackagePath).IsValid; }
            catch (InvalidOperationException) { rejected = true; }
            Check.That(rejected, "independent portable v2 inspection rejects " + name + " mismatch");
        }
        foreach (var multipleSlots in new[] { false, true })
        {
            var fixture = new Owned(Path.Combine(root, "lifecycle-v2-" + (multipleSlots ? "multiple-texture-slots" : "unbound-consuming-layer")));
            var prepared = VfxPreparation.Prepare(fixture.Workspace(), Owned.RequestPath, Output, Build);
            var package = JsonNode.Parse(File.ReadAllBytes(fixture.Full(prepared.PackagePath)))!.AsObject();
            var layers = package["effects"]![0]!["layers"]!.AsArray();
            var second = layers[0]!.DeepClone().AsObject(); second["id"] = "second"; layers.Add(second);
            package["budgets"]!["maxLayers"] = 2; package["budgets"]!["maxParticles"] = 2;
            package["budgets"]!["maxVertices"] = 8; package["budgets"]!["maxDrawCalls"] = 2;
            if (multipleSlots)
            {
                var resources = package["resources"]!.AsArray();
                var resource = resources[0]!.DeepClone().AsObject(); resource["id"] = "atlas-other"; resource["path"] = "atlas-other.png"; resources.Add(resource);
                File.Copy(fixture.Full(Output + "/atlas.png"), fixture.Full(Output + "/atlas-other.png"));
                layers[0]!["resourceIds"] = new JsonArray("atlas", "atlas-other"); second["resourceIds"] = new JsonArray("atlas-other");
                package["budgets"]!["maxDependencies"] = 2;
                package["budgets"]!["maxTotalDependencyBytes"] = resources[0]!["bytes"]!.GetValue<long>() * 2;
                var atlases = package["atlases"]!.AsArray();
                var metadata = atlases[0]!.DeepClone().AsObject(); metadata["resourceId"] = "atlas-other";
                metadata["bindings"]![0]!["layerId"] = "second"; atlases.Add(metadata);
            }
            var report = VfxValidation.Validate(VfxJson.Parse(JsonSerializer.SerializeToUtf8Bytes(package)));
            Check.That(!report.IsValid && report.Issues.Any(x => x.Code == "invalid-atlas") && report.Issues.All(x => x.Code == "invalid-atlas"),
                "portable v2 specifically rejects " + (multipleSlots ? "multiple PNG texture slots in one layer" : "an unbound layer that consumes an atlas"));
        }

        foreach (var (name, mutate) in new (string, Action<Owned, JsonObject>)[]
        {
            ("request", (f, _) => File.AppendAllText(f.Full(Owned.RequestPath), " ")),
            ("source", (f, _) => File.AppendAllText(f.Full("inputs/preparation/frame-0.png"), "changed")),
            ("package", (f, p) => File.AppendAllText(f.Full(p["packagePath"]!.GetValue<string>()), " ")),
            ("version reservation", (f, _) => File.AppendAllText(f.Full(".bubo/state/vfx-preparation/owned-atlas/v1.json"), " ")),
            ("extra inventory file", (f, _) => File.WriteAllText(f.Full(Output + "/undeclared.txt"), "unowned"))
        })
        {
            var fixture = new Owned(Path.Combine(root, "lifecycle-drift-" + name.Replace(' ', '-')));
            var prepared = VfxPreparation.Prepare(fixture.Workspace(), Owned.RequestPath, Output, Build);
            mutate(fixture, Data(prepared));
            Reject(() => VfxPreparation.Verify(fixture.Workspace(), prepared.ManifestPath, Build), "exact preparation verification rejects drift in " + name);
        }
        foreach (var path in new[] { "../escape", "inputs/preparation/output", "assets/runtime-effect", ".art-staging/prepared/../alias", ".art-staging//alias", ".git/output" })
        {
            var fixture = new Owned(Path.Combine(root, "lifecycle-path-" + Guid.NewGuid().ToString("N")));
            Reject(() => VfxPreparation.Prepare(fixture.Workspace(), Owned.RequestPath, path, Build), "preparation refuses unsafe/nonstaging destination " + path);
        }
        var disk = new Owned(Path.Combine(root, "lifecycle-disk")); disk.Request["budgets"]!["minimumFreeDiskBytes"] = long.MaxValue; disk.Save();
        Reject(() => VfxPreparation.Prepare(disk.Workspace(), Owned.RequestPath, Output, Build), "preparation refuses impossible minimum-free-disk declaration");
        var hard = new Owned(Path.Combine(root, "lifecycle-hard-memory")); hard.Request["budgets"]!["hardProcessMemoryBytes"] = 67108864; hard.Save();
        Reject(() => VfxPreparation.Prepare(hard.Workspace(), Owned.RequestPath, Output, Build), "unimplemented hard process memory request remains unavailable");
        var during = new Owned(Path.Combine(root, "lifecycle-source-drift-during-build"));
        var changing = new MutationWorkspace(during.Workspace(), () => File.AppendAllText(during.Full("inputs/preparation/frame-0.png"), "changed-during-build"));
        Reject(() => VfxPreparation.Prepare(changing, Owned.RequestPath, Output, Build), "original source mutation during real output writing prevents sealing");
        Check.That(!File.Exists(during.Full(Output + "/preparation.json")), "source drift during processing preserves an unsealed attempt");
        var link = new Owned(Path.Combine(root, "lifecycle-output-link"));
        Directory.CreateDirectory(link.Full(".art-staging")); Directory.CreateDirectory(link.Full("owned-link-target"));
        try
        {
            Directory.CreateSymbolicLink(link.Full(".art-staging/linked"), link.Full("owned-link-target"));
            Reject(() => VfxPreparation.Prepare(link.Workspace(), Owned.RequestPath, ".art-staging/linked/v1", Build), "actual owned symbolic-link staging parent cannot redirect processing writes");
            Check.That(!Directory.EnumerateFileSystemEntries(link.Full("owned-link-target")).Any(), "rejected staging link leaves its target untouched");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException
            || error is IOException && OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 1314)
        { Check.Limits.Add("Actual preparation staging symlink check unavailable: " + error.GetType().Name); }
        foreach (var (name, mutate) in new (string, Action<Owned>)[]
        {
            ("damaged actual input", f => f.RefreshSource(1, CorruptCrc(File.ReadAllBytes(f.Full("inputs/preparation/frame-1.png"))))),
            ("output bytes", f => f.Request["budgets"]!["maxOutputBytes"] = 1024),
            ("file count", f => f.Request["budgets"]!["maxOutputFiles"] = 1)
        })
        {
            var fixture = new Owned(Path.Combine(root, "lifecycle-failure-" + name.Replace(' ', '-'))); mutate(fixture); fixture.Save();
            Reject(() => VfxPreparation.Prepare(fixture.Workspace(), Owned.RequestPath, Output, Build), "bounded attempt fails for " + name);
            Check.That(!File.Exists(fixture.Full(Output + "/preparation.json")), name + " never leaves a successful seal");
            if (Directory.Exists(fixture.Full(Output)))
            {
                var retained = fixture.Host.Snapshot();
                Reject(() => VfxPreparation.Prepare(fixture.Workspace(), Owned.RequestPath, Output, Build), name + " failed attempt cannot be reset or overwritten");
                Check.That(InspectionTests.Equal(retained, fixture.Host.Snapshot()), name + " failure history and partial files remain byte-identical");
            }
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var cancellation = new Owned(Path.Combine(root, "lifecycle-canceled"));
        Check.Throws<OperationCanceledException>(() => VfxPreparation.Prepare(cancellation.Workspace(), Owned.RequestPath, Output, Build, canceled.Token), "preparation cancellation creates no successful seal");
        Check.That(!File.Exists(cancellation.Full(Output + "/preparation.json")), "pre-canceled attempt has no sealed manifest");
    }

    static void Cli(string cli, string root)
    {
        var fixture = new Owned(Path.Combine(root, "cli-preparation"));
        var prepared = Execute(cli, Arguments(fixture, "prepare", "--request", Owned.RequestPath, "--output", Output));
        Check.That(prepared.ExitCode == 0 && prepared.Json["status"]?.GetValue<string>() == "succeeded", "actual public CLI prepares exact owned image sequence");
        var manifest = prepared.Json["data"]!["manifestPath"]!.GetValue<string>();
        fixture.Host.Context["references"]!.AsArray().Add(fixture.Host.Reference(manifest, "input")); fixture.Host.SaveContext();
        var verified = Execute(cli, Arguments(fixture, "verify-preparation", "--manifest", manifest));
        Check.That(verified.ExitCode == 0 && verified.Json["status"]?.GetValue<string>() == "succeeded", "actual public CLI independently verifies prepared portable payload");
        var collision = Execute(cli, Arguments(fixture, "prepare", "--request", Owned.RequestPath, "--output", Output));
        Check.That(collision.ExitCode != 0, "actual public CLI refuses repeated immutable output directory");
        var stale = new Owned(Path.Combine(root, "cli-stale-source"));
        File.AppendAllText(stale.Full("inputs/preparation/frame-1.png"), "drift");
        Check.That(Execute(cli, Arguments(stale, "prepare", "--request", Owned.RequestPath, "--output", Output)).ExitCode != 0, "actual public CLI rejects stale exact source reference before successful preparation");
        var wrongPolicy = new Owned(Path.Combine(root, "cli-wrong-policy")); wrongPolicy.Request["styleSha256"] = new string('f', 64); wrongPolicy.Save();
        Check.That(Execute(cli, Arguments(wrongPolicy, "prepare", "--request", Owned.RequestPath, "--output", Output)).ExitCode != 0, "actual public CLI refuses preparation with transplanted STYLE identity");
        foreach (var option in new[] { "--decoder", "--provider", "--script" })
            Check.That(Execute(cli, Arguments(fixture, "prepare", "--request", Owned.RequestPath, "--output", ".art-staging/no-install", option, "untrusted")).ExitCode != 0, "actual public CLI refuses undeclared processing authority " + option);
        Interrupt(cli, root);
    }

    static void ContrastDiagnostics(string root)
    {
        var fixture = Path.Combine(root, "lifecycle-good");
        var packagePath = Path.Combine(fixture, Output.Replace('/', Path.DirectorySeparatorChar), "package.json");
        var package = VfxJson.Parse(File.ReadAllBytes(packagePath));
        var atlasPath = Path.Combine(fixture, Output.Replace('/', Path.DirectorySeparatorChar), "atlas.png");
        var atlas = RgbaPngCodec.Read(atlasPath, new() { ColorSpace = "srgb" });
        var sourcePaths = Enumerable.Range(0, 3).Select(i => Path.Combine(fixture, "inputs", "preparation", "frame-" + i + ".png")).ToArray();
        var sources = sourcePaths.Select(p => RgbaPngCodec.Read(p, new() { ColorSpace = "srgb" })).ToArray();
        var order = new[] { 0, 1, 2, 0 };
        const int scale = 40, margin = 16, columnWidth = 112, rowHeight = 152;
        var width = margin * 2 + columnWidth * order.Length; var height = margin * 2 + rowHeight * 4;
        var sheet = new byte[width * height * 4];
        Fill(sheet, 28, 35, 43);
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < order.Length; column++)
        {
            var sourceIndex = order[column];
            var frame = package.Atlases![0].Frames[sourceIndex];
            var raw = row % 2 == 0 ? sources[sourceIndex].Pixels : Extract(atlas.Pixels, atlas.Width, frame.X, frame.Y, frame.Width, frame.Height);
            var background = row < 2 ? new byte[] { 12, 18, 24 } : new byte[] { 238, 243, 247 };
            var expected = Composite(sources[sourceIndex].Pixels, background);
            var actual = Composite(raw, background);
            Check.That(actual.SequenceEqual(expected), "actual source/prepared alpha composites agree on " + (row < 2 ? "dark" : "light") + " background, sequence column " + column);
            for (var y = 0; y < 3 * scale; y++)
            for (var x = 0; x < 2 * scale; x++)
            {
                var from = ((y / scale) * 2 + x / scale) * 4;
                var targetX = margin + column * columnWidth + (columnWidth - 2 * scale) / 2 + x;
                var targetY = margin + row * rowHeight + (rowHeight - 3 * scale) / 2 + y;
                actual.AsSpan(from, 4).CopyTo(sheet.AsSpan((targetY * width + targetX) * 4, 4));
            }
        }
        var directory = Path.Combine(root, "diagnostics"); Directory.CreateDirectory(directory);
        var contrastPath = Path.Combine(directory, "contrast-first-last-repeat.png");
        RgbaPngCodec.Write(contrastPath, new(width, height, sheet), new() { ColorSpace = "srgb" });
        const int atlasScale = 32;
        var checkerWidth = atlas.Width * atlasScale; var checkerHeight = atlas.Height * atlasScale;
        var checker = new byte[checkerWidth * checkerHeight * 4];
        for (var y = 0; y < checkerHeight; y++)
        for (var x = 0; x < checkerWidth; x++)
        {
            var source = ((y / atlasScale) * atlas.Width + x / atlasScale) * 4;
            var target = (y * checkerWidth + x) * 4;
            var bg = ((x / atlasScale + y / atlasScale) & 1) == 0 ? (byte)56 : (byte)218;
            for (var c = 0; c < 3; c++) checker[target + c] = (byte)((atlas.Pixels[source + c] * atlas.Pixels[source + 3] + bg * (255 - atlas.Pixels[source + 3]) + 127) / 255);
            checker[target + 3] = 255;
        }
        var guttersPath = Path.Combine(directory, "atlas-gutter-checker.png");
        RgbaPngCodec.Write(guttersPath, new(checkerWidth, checkerHeight, checker), new() { ColorSpace = "srgb" });
        File.WriteAllBytes(Path.Combine(directory, "diagnostics.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = "bubo-vfx-pixel-diagnostics/v1",
            sourceFiles = sourcePaths.Select(p => new { path = p, sha256 = Workspace.Hash(p) }),
            atlas = new { path = atlasPath, sha256 = Workspace.Hash(atlasPath) },
            package = new { path = packagePath, sha256 = Workspace.Hash(packagePath) },
            contrast = new { path = contrastPath, sha256 = Workspace.Hash(contrastPath), columns = new[] { "first:0", "middle:1", "last:2", "repeat-first:0" }, rows = new[] { "original over dark", "prepared over dark", "original over light", "prepared over light" }, scale = "nearest40x" },
            gutters = new { path = guttersPath, sha256 = Workspace.Hash(guttersPath), scale = "nearest32x", origin = "top-left", cellWidth = 6, cellHeight = 7, interiorWidth = 2, interiorHeight = 3, transparentPadding = 1, edgeExtrusion = 1 },
            limits = new[] { "CPU encoded-sample integer alpha-composite diagnostic only; no engine or GPU color-management claim", "Frame order repeats declared sequence; endpoints do not establish seamless-loop quality", "Owned synthetic pixels; no artistic, human, runtime or promotion acceptance" }
        }, Workspace.Json));
        Console.WriteLine("PIXEL DIAGNOSTICS: " + directory);
    }

    static void Fill(byte[] rgba, byte r, byte g, byte b)
    {
        for (var i = 0; i < rgba.Length; i += 4) { rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = 255; }
    }
    static byte[] Composite(byte[] rgba, byte[] background)
    {
        var result = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            for (var c = 0; c < 3; c++) result[i + c] = (byte)((rgba[i + c] * rgba[i + 3] + background[c] * (255 - rgba[i + 3]) + 127) / 255);
            result[i + 3] = 255;
        }
        return result;
    }

    static void Interrupt(string cli, string root)
    {
        var fixture = new Owned(Path.Combine(root, "cli-owned-interruption"), "math-mask");
        fixture.Request["mask"]!["width"] = 4092; fixture.Request["mask"]!["height"] = 4092;
        fixture.Request["budgets"]!["maxDecodedPixels"] = 16777216;
        fixture.Request["budgets"]!["maxAtlasPixels"] = 16777216;
        fixture.Request["budgets"]!["maxPixelWork"] = 1073741824;
        fixture.Request["budgets"]!["maxWorkingBytes"] = 536870912;
        fixture.Request["budgets"]!["maxOutputBytes"] = 16777216;
        fixture.Save();
        var start = StartInfo(cli, Arguments(fixture, "prepare", "--request", Owned.RequestPath, "--output", Output));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start owned interruption fixture");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        var watch = Stopwatch.StartNew();
        var started = fixture.Full(Output + "/started.json");
        while (!File.Exists(started) && !process.HasExited && watch.ElapsedMilliseconds < 15000) Thread.Sleep(1);
        if (!File.Exists(started))
        {
            if (!process.HasExited) process.Kill(true);
            process.WaitForExit(); Task.WaitAll(stdout, stderr);
            throw new InvalidOperationException("Owned interruption fixture did not reach started receipt: " + stdout.Result + stderr.Result);
        }
        process.Kill(true); process.WaitForExit(); Task.WaitAll(stdout, stderr);
        Check.That(process.HasExited && File.Exists(started), "actual owned CLI interruption retains started processing attempt after process retirement");
        Check.That(!File.Exists(fixture.Full(Output + "/preparation.json")), "actual interrupted preparation never seals a portable success");
        var retained = fixture.Host.Snapshot();
        Check.That(Execute(cli, Arguments(fixture, "prepare", "--request", Owned.RequestPath, "--output", Output)).ExitCode != 0, "interrupted output cannot be silently retried or reset");
        Check.That(InspectionTests.Equal(retained, fixture.Host.Snapshot()), "refused interrupted retry preserves all known partial bytes and reservation");
        Check.That(Execute(cli, Arguments(fixture, "prepare", "--request", Owned.RequestPath, "--output", ".art-staging/redirected-interrupted-version")).ExitCode != 0, "moving interrupted output cannot reset the permanent version reservation");
        Check.That(InspectionTests.Equal(retained, fixture.Host.Snapshot()), "refused redirected interruption preserves permanent reservation and partial inventory");
        fixture.Request["version"] = "v2"; fixture.Request["mask"]!["width"] = 9; fixture.Request["mask"]!["height"] = 7; fixture.Save();
        var continuation = Execute(cli, Arguments(fixture, "prepare", "--request", Owned.RequestPath, "--output", ".art-staging/prepared/v2"));
        Check.That(continuation.ExitCode == 0 && File.Exists(fixture.Full(".art-staging/prepared/v2/preparation.json")), "explicit new version continues after interruption while retaining original attempt");
        Check.That(File.Exists(started) && !File.Exists(fixture.Full(Output + "/preparation.json")), "successful continuation neither clears nor seals interrupted history");
    }

    sealed class CancelingReadStream(byte[] bytes, CancellationTokenSource source) : MemoryStream(bytes)
    {
        int reads;
        public override int Read(Span<byte> buffer)
        {
            var read = base.Read(buffer);
            if (++reads == 3) source.Cancel();
            return read;
        }
    }

    sealed class MutationWorkspace(IWorkspace workspace, Action mutate) : IWorkspace
    {
        bool changed;
        public string Root => workspace.Root;
        public string Resolve(string path) => workspace.Resolve(path);
        public string Relative(string path) => workspace.Relative(path);
        public string HashFile(string path) => workspace.HashFile(path);
        public void Save<T>(string path, T value) => workspace.Save(path, value);
        public T Load<T>(string path) => workspace.Load<T>(path);
        public FileStream Lock(string path) => workspace.Lock(path);
        public string ResolveWrite(string path)
        {
            var resolved = workspace.ResolveWrite(path);
            if (!changed && path.EndsWith("/atlas.png", StringComparison.Ordinal)) { changed = true; mutate(); }
            return resolved;
        }
    }

    static string[] Arguments(Owned fixture, string operation, params string[] arguments) => new[] { "vfx-assets", operation, "--api-version", "2", "--root", fixture.Root, "--host-context", "host-context.json" }.Concat(arguments).ToArray();
    static (int ExitCode, JsonObject Json) Execute(string cli, string[] arguments)
    {
        var start = StartInfo(cli, arguments);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start existing built CLI");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new InvalidOperationException("Preparation CLI fixture exceeded 30-second owned watchdog"); }
        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, JsonNode.Parse(stdout.Result)?.AsObject() ?? throw new InvalidOperationException("CLI returned no JSON: " + stderr.Result));
    }

    static ProcessStartInfo StartInfo(string cli, string[] arguments)
    {
        var start = new ProcessStartInfo(cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : cli)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
}
