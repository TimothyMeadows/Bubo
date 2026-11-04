using Hephaera.AssetProduction;

namespace Hephaera.ProofToModel.Infrastructure;

/// <summary>Compatibility facade over the shared bounded PNG codec; alpha padding remains unchanged.</summary>
public static class RgbaPng
{
    public sealed record Image(int Width, int Height, byte[] Pixels);
    public const int MaximumDimension = RgbaPngCodec.MaximumDimension;
    public const int MaximumPixels = RgbaPngCodec.MaximumPixels;

    public static Image Read(string path)
    {
        var image = RgbaPngCodec.Read(path);
        return new(image.Width, image.Height, image.Pixels);
    }
    public static void Write(string path, Image image) =>
        RgbaPngCodec.Write(path, new(image.Width, image.Height, image.Pixels));

    public static void PadSquare(string input, string output, int side)
    {
        Dimensions(side, side);
        if (Path.GetFullPath(input).Equals(Path.GetFullPath(output), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Padding must preserve its input and write a new working copy");
        if (File.Exists(output) || Directory.Exists(output))
            throw new InvalidOperationException("Padding never overwrites an existing artifact");
        var source = Read(input);
        if (side < source.Width || side < source.Height)
            throw new InvalidOperationException("Padding canvas cannot crop or resize the source");
        if (!Enumerable.Range(0, source.Width * source.Height).Any(i => source.Pixels[i * 4 + 3] > 0))
            throw new InvalidOperationException("Padding rejects an empty foreground");
        if (!Enumerable.Range(0, source.Width * source.Height).Any(i => source.Pixels[i * 4 + 3] < 250))
            throw new InvalidOperationException("Padding requires a real alpha matte; it cannot remove a background");
        var pixels = new byte[checked(side * side * 4)];
        var left = (side - source.Width) / 2;
        var top = (side - source.Height) / 2;
        for (var y = 0; y < source.Height; y++)
            source.Pixels.AsSpan(y * source.Width * 4, source.Width * 4).CopyTo(pixels.AsSpan(((y + top) * side + left) * 4));
        Write(output, new(side, side, pixels));
    }
    static void Dimensions(int width, int height)
    {
        if (width < 1 || height < 1 || width > MaximumDimension || height > MaximumDimension || (long)width * height > MaximumPixels)
            throw new InvalidOperationException("PNG dimensions exceed the bounded pixel budget");
    }
}
