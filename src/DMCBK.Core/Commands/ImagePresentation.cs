namespace DMCBK.Core.Commands;

/// <summary>A host-neutral RGB pixel.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
public readonly record struct RgbPixel(byte R, byte G, byte B);

/// <summary>An immutable RGB frame supplied to a presentation host.</summary>
public sealed class RgbImageFrame
{
    private readonly byte[] _pixels;
    /// <summary>Copies a bounded row-major RGB frame without retaining the caller's mutable buffer.</summary>
    public RgbImageFrame(int width, int height, ReadOnlySpan<byte> pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if ((long)width * height > 16 * 1024 * 1024 || pixels.Length != (long)width * height * 3)
            throw new ArgumentException("presentation.image-dimensions", nameof(pixels));
        Width = width; Height = height; _pixels = pixels.ToArray();
    }
    /// <summary>Width in pixels.</summary>
    public int Width { get; }
    /// <summary>Height in pixels.</summary>
    public int Height { get; }
    /// <summary>Gets one pixel without exposing the backing buffer.</summary>
    public RgbPixel GetPixel(int x, int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x); ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width); ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        int offset = (y * Width + x) * 3;
        return new(_pixels[offset], _pixels[offset + 1], _pixels[offset + 2]);
    }
}

/// <summary>An image presentation request independent of terminal dimensions or rendering frameworks.</summary>
/// <param name="Image">The immutable pixel snapshot.</param>
/// <param name="Title">The localized presentation title.</param>
/// <param name="Output">The optional command output associated with the request.</param>
public sealed record ImagePresentationRequest(RgbImageFrame Image, string? Title = null, ICommandOutput? Output = null);
