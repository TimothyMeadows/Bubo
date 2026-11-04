using System.Diagnostics;
using Bubo.Vfx.Domain;
using Hephaera.AssetProduction;

namespace Bubo.Vfx.Application;

/// <summary>Owned pixel buffers and measured work; no filesystem, provider, renderer or process authority.</summary>
public sealed record VfxPreparedPixels(RgbaPngCodec.Image Image, VfxAtlasMetadata Atlas,
    long DecodedPixels, long PixelWork, long EstimatedWorkingBytes);

/// <summary>
/// Deterministic fixed-input processing. The caller admits exact source bytes and owns immutable output streams.
/// Pixel work reserves initialization, encoding, independent reopening and comparison of the complete atlas.
/// Managed live-buffer estimates are not an enforced process-memory limit.
/// </summary>
public static class VfxPixelPreparation
{
    public static VfxPreparedPixels Render(VfxPreparationRequest request,
        Func<VfxPreparationSource, RgbaPngCodec.Limits, CancellationToken, RgbaPngCodec.Image> read,
        CancellationToken cancellation = default)
    {
        PreparationValidation.RequireValid(request);
        ArgumentNullException.ThrowIfNull(read);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.Budgets.MaxSeconds));
        var token = timeout.Token;
        var clock = Stopwatch.StartNew();
        var budget = request.Budgets;
        long decoded = 0, work = 0;
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed.TotalSeconds > budget.MaxSeconds) throw new InvalidOperationException("VFX pixel processing exceeded time budget");
        }
        void Work(long pixels)
        {
            Check();
            if (pixels < 0 || pixels > budget.MaxPixelWork - work) throw new InvalidOperationException("VFX pixel-work budget exceeded");
            work += pixels;
        }
        var codec = new RgbaPngCodec.Limits
        {
            MaxEncodedBytes = budget.MaxSourceBytes, MaxChunkBytes = budget.MaxChunkBytes,
            MaxCompressedBytes = budget.MaxCompressedBytes, MaxPixels = Math.Min(budget.MaxDecodedPixels, budget.MaxPixelWork),
            MaxWorkingBytes = budget.MaxWorkingBytes,
            MaxOutputBytes = Math.Min(budget.MaxOutputBytes, 134217728), ColorSpace = request.Atlas.ColorSpace
        };
        RgbaPngCodec.Image Read(VfxPreparationSource source, RgbaPngCodec.Limits limits, bool workReserved = false)
        {
            Check();
            var image = read(source, limits, token);
            Check();
            if (image is null || image.Width < 1 || image.Height < 1 || image.Width > RgbaPngCodec.MaximumDimension || image.Height > RgbaPngCodec.MaximumDimension
                || (long)image.Width * image.Height > limits.MaxPixels || image.Pixels is null || image.Pixels.Length != checked(image.Width * image.Height * 4))
                throw new InvalidOperationException("PNG port returned an invalid or oversized image");
            var pixels = checked((long)image.Width * image.Height);
            decoded = checked(decoded + pixels);
            if (!workReserved) Work(pixels);
            return image;
        }

        var count = request.Timing.FrameCount;
        var first = request.Timing.FirstFrame;
        RgbaPngCodec.Image? sourceImage = null;
        int width, height;
        if (request.Profile == "math-mask")
        {
            width = request.Mask!.Width; height = request.Mask.Height;
        }
        else
        {
            sourceImage = Read(request.Sources[request.Profile == "sheet" ? 0 : first], codec);
            width = request.Profile == "sheet" ? request.Sheet!.CellWidth : sourceImage.Width;
            height = request.Profile == "sheet" ? request.Sheet!.CellHeight : sourceImage.Height;
            if (request.Profile == "sheet")
            {
                var sheet = request.Sheet!;
                if (checked((long)sheet.OriginX + (long)(sheet.Columns - 1) * sheet.StrideX + width) > sourceImage.Width
                    || checked((long)sheet.OriginY + (long)(sheet.Rows - 1) * sheet.StrideY + height) > sourceImage.Height)
                    throw new InvalidOperationException("Declared sheet grid lies outside actual source pixels");
            }
        }
        var border = checked(request.Atlas.Padding + request.Atlas.Extrusion);
        var cellWidth = checked(width + 2 * border);
        var cellHeight = checked(height + 2 * border);
        var rows = checked((count + request.Atlas.Columns - 1) / request.Atlas.Columns);
        var atlasWidth = checked(cellWidth * request.Atlas.Columns);
        var atlasHeight = checked(cellHeight * rows);
        var atlasPixels = checked((long)atlasWidth * atlasHeight);
        if (atlasWidth > RgbaPngCodec.MaximumDimension || atlasHeight > RgbaPngCodec.MaximumDimension || atlasPixels > budget.MaxAtlasPixels)
            throw new InvalidOperationException("Actual atlas exceeds declared dimension or pixel budget");
        var inputPixels = request.Profile == "sheet" ? checked((long)sourceImage!.Width * sourceImage.Height) : checked((long)width * height);
        var inputRowWidth = request.Profile == "sheet" ? sourceImage!.Width : width;
        // Two atlas buffers allow the independent verifier to reopen while retaining expected pixels.
        // One input plus decoder rows, bounded chunk/descriptors, encoder chunk and metadata coexist.
        var estimatedWorking = checked(atlasPixels * 8 + inputPixels * 4 + inputRowWidth * 8L
            + RgbaPngCodec.FixedWorkingBytes + RgbaPngCodec.BufferBytes + count * 256L);
        if (estimatedWorking > budget.MaxWorkingBytes) throw new InvalidOperationException("VFX live owned-buffer estimate exceeds working-allocation budget");
        Work(checked(atlasPixels * 4));
        if (request.Profile == "math-mask") Work(inputPixels);
        var writtenPixels = checked((long)(width + request.Atlas.Extrusion * 2) * (height + request.Atlas.Extrusion * 2) * count);
        Work(writtenPixels);
        Check();
        var pixels = new byte[checked((int)(atlasPixels * 4))];
        var frames = new List<VfxAtlasFrame>(count);
        if (request.Profile == "math-mask") sourceImage = Mask(request.Mask!, token);
        var remainingCodec = codec with
        {
            MaxPixels = Math.Min(budget.MaxDecodedPixels, inputPixels),
            MaxWorkingBytes = checked(budget.MaxWorkingBytes - atlasPixels * 4 - count * 256L)
        };
        for (var frame = 0; frame < count; frame++)
        {
            Check();
            var sourceFrame = checked(first + frame * request.Timing.FrameStep);
            if (request.Profile == "sequence" && frame != 0)
            {
                Work(inputPixels);
                sourceImage = null;
                sourceImage = Read(request.Sources[sourceFrame], remainingCodec, true);
                if (sourceImage.Width != width || sourceImage.Height != height) throw new InvalidOperationException("Sequence frames must have exactly identical dimensions");
            }
            var sourceX = 0; var sourceY = 0;
            var sourceIdentity = sourceFrame;
            if (request.Profile == "sheet")
            {
                var sheet = request.Sheet!;
                sourceIdentity = sheet.FrameIndices[sourceFrame];
                sourceX = checked(sheet.OriginX + sourceIdentity % sheet.Columns * sheet.StrideX);
                sourceY = checked(sheet.OriginY + sourceIdentity / sheet.Columns * sheet.StrideY);
            }
            var cellX = frame % request.Atlas.Columns * cellWidth;
            var cellY = frame / request.Atlas.Columns * cellHeight;
            var x = cellX + border; var y = cellY + border;
            var extrusion = request.Atlas.Extrusion;
            // Extrusion copies all four RGBA channels; the outer padding remains transparent black.
            for (var targetY = -extrusion; targetY < height + extrusion; targetY++)
            {
                Check();
                var readY = sourceY + Math.Clamp(targetY, 0, height - 1);
                for (var targetX = -extrusion; targetX < width + extrusion; targetX++)
                {
                    if ((targetX & 4095) == 0) Check();
                    var readX = sourceX + Math.Clamp(targetX, 0, width - 1);
                    var from = checked((readY * sourceImage!.Width + readX) * 4);
                    var to = checked(((y + targetY) * atlasWidth + x + targetX) * 4);
                    sourceImage.Pixels.AsSpan(from, 4).CopyTo(pixels.AsSpan(to, 4));
                }
            }
            frames.Add(new VfxAtlasFrame
            {
                SourceFrame = sourceIdentity, CellX = cellX, CellY = cellY, CellWidth = cellWidth, CellHeight = cellHeight,
                X = x, Y = y, Width = width, Height = height
            });
        }
        var numerator = checked((long)count * request.Timing.RateDenominator);
        var denominator = request.Timing.RateNumerator;
        var divisor = Gcd(numerator, denominator);
        var atlas = new VfxAtlasMetadata
        {
            ResourceId = "atlas", Width = atlasWidth, Height = atlasHeight, Origin = "top-left",
            Alpha = request.Atlas.Alpha, ColorSpace = request.Atlas.ColorSpace,
            Padding = request.Atlas.Padding, Extrusion = request.Atlas.Extrusion, Frames = frames.AsReadOnly(),
            RateNumerator = request.Timing.RateNumerator, RateDenominator = request.Timing.RateDenominator, Loop = request.Timing.Loop,
            DurationNumerator = numerator / divisor, DurationDenominator = checked((int)(denominator / divisor)),
            Bindings = Array.AsReadOnly(new[] { new VfxAtlasBinding { EffectId = request.Id, LayerId = "sprite" } })
        };
        Check();
        return new(new(atlasWidth, atlasHeight, pixels), atlas, decoded, work, estimatedWorking);
    }

    static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return a; }

    static RgbaPngCodec.Image Mask(VfxMathMask mask, CancellationToken cancellation)
    {
        var pixels = new byte[checked(mask.Width * mask.Height * 4)];
        var angle = mask.AngleDegrees * Math.PI / 180;
        var cos = Math.Cos(angle); var sin = Math.Sin(angle);
        for (var y = 0; y < mask.Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            var ny = 2.0 * (y + .5) / mask.Height - 1;
            for (var x = 0; x < mask.Width; x++)
            {
                if ((x & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                var nx = 2.0 * (x + .5) / mask.Width - 1;
                var radius = Math.Sqrt(nx * nx + ny * ny);
                var outer = mask.Softness == 0 ? (radius <= mask.OuterRadius ? 1.0 : 0.0) : Math.Clamp((mask.OuterRadius - radius) / mask.Softness, 0, 1);
                double alpha;
                if (mask.Kind == "disc") alpha = outer;
                else if (mask.Kind == "ring")
                {
                    var inner = mask.Softness == 0 ? (radius >= mask.InnerRadius ? 1.0 : 0.0) : Math.Clamp((radius - mask.InnerRadius) / mask.Softness, 0, 1);
                    alpha = Math.Min(inner, outer);
                }
                else
                {
                    var t = Math.Clamp(((nx * cos + ny * sin + 1) / 2 - mask.InnerRadius) / (mask.OuterRadius - mask.InnerRadius), 0, 1);
                    alpha = t * (1 - mask.Softness) + t * t * (3 - 2 * t) * mask.Softness;
                }
                var i = (y * mask.Width + x) * 4;
                pixels[i] = 255; pixels[i + 1] = 255; pixels[i + 2] = 255;
                pixels[i + 3] = (byte)Math.Clamp((int)Math.Round(alpha * 255, MidpointRounding.AwayFromZero), 0, 255);
            }
        }
        return new(mask.Width, mask.Height, pixels);
    }
}
