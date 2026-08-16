using System.Globalization;
using SkiaSharp;

namespace Cratebase.Storage;

/// <summary>Requested thumbnail size.</summary>
/// <param name="Width">Width, or 0 to "derive from ratio".</param>
/// <param name="Height">Height, or 0 to "derive from ratio".</param>
/// <param name="Mode">Cropping mode.</param>
public readonly record struct ThumbSize(int Width, int Height, ThumbMode Mode)
{
    /// <summary>Canonical form, as used as a cache key.</summary>
    public override string ToString() =>
        $"{Width}x{Height}{(Mode is ThumbMode.Crop ? "" : Suffix(Mode))}";

    private static string Suffix(ThumbMode mode) => mode switch
    {
        ThumbMode.Top => "t",
        ThumbMode.Bottom => "b",
        ThumbMode.Fit => "f",
        _ => "",
    };
}

/// <summary>Thumbnail cropping mode.</summary>
public enum ThumbMode
{
    /// <summary>Centered crop.</summary>
    Crop,

    /// <summary>Crop anchored to the top.</summary>
    Top,

    /// <summary>Crop anchored to the bottom.</summary>
    Bottom,

    /// <summary>Whole content, no cropping.</summary>
    Fit,
}

/// <summary>
/// Thumbnail generation.
/// </summary>
/// <remarks>
/// Syntax borrowed from PocketBase: <c>WxH</c>, <c>WxHt</c>, <c>WxHb</c>, <c>WxHf</c>, <c>0xH</c>,
/// <c>Wx0</c>. A client written against PocketBase has nothing new to learn.
/// </remarks>
public static class ThumbnailGenerator
{
    /// <summary>Maximum thumbnail dimension, in pixels.</summary>
    /// <remarks>
    /// A mandatory bound: without it, <c>?thumb=20000x20000</c> asks the server to allocate more
    /// than a gigabyte, from a single URL and with no authentication if the file is public.
    /// </remarks>
    public const int MaxDimension = 4000;

    /// <summary>Supported input formats.</summary>
    public static bool IsSupported(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

    /// <summary>Parses a size expression.</summary>
    public static bool TryParse(string? raw, out ThumbSize size)
    {
        size = default;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var mode = raw[^1] switch
        {
            't' => ThumbMode.Top,
            'b' => ThumbMode.Bottom,
            'f' => ThumbMode.Fit,
            _ => ThumbMode.Crop,
        };

        var body = mode is ThumbMode.Crop ? raw : raw[..^1];
        var parts = body.Split('x');

        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height))
        {
            return false;
        }

        if (width is < 0 or > MaxDimension || height is < 0 or > MaxDimension || (width == 0 && height == 0))
        {
            return false;
        }

        size = new ThumbSize(width, height, mode);
        return true;
    }

    /// <summary>
    /// Produces a thumbnail in PNG format.
    /// </summary>
    /// <returns>The thumbnail bytes, or <see langword="null"/> if the source is unreadable.</returns>
    public static byte[]? Generate(Stream source, ThumbSize size)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var original = SKBitmap.Decode(source);

        if (original is null)
        {
            return null;
        }

        var (width, height) = Resolve(original.Width, original.Height, size);

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        using var surface = new SKBitmap(width, height);
        using var canvas = new SKCanvas(surface);

        canvas.Clear(SKColors.Transparent);

        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);

        if (size.Mode is ThumbMode.Fit || size.Width == 0 || size.Height == 0)
        {
            canvas.DrawBitmap(original, new SKRect(0, 0, width, height), sampling);
        }
        else
        {
            canvas.DrawBitmap(original, SourceRectFor(original, width, height, size.Mode),
                new SKRect(0, 0, width, height), sampling);
        }

        canvas.Flush();

        using var image = SKImage.FromBitmap(surface);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);

        return data?.ToArray();
    }

    private static (int Width, int Height) Resolve(int sourceWidth, int sourceHeight, ThumbSize size)
    {
        // A dimension of zero means "derive from ratio": that's the most useful form for a client,
        // which knows its display width but not the image's height.
        if (size.Width == 0)
        {
            return ((int)Math.Round(sourceWidth * (double)size.Height / sourceHeight), size.Height);
        }

        if (size.Height == 0)
        {
            return (size.Width, (int)Math.Round(sourceHeight * (double)size.Width / sourceWidth));
        }

        if (size.Mode is not ThumbMode.Fit)
        {
            return (size.Width, size.Height);
        }

        var ratio = Math.Min(size.Width / (double)sourceWidth, size.Height / (double)sourceHeight);

        return ((int)Math.Round(sourceWidth * ratio), (int)Math.Round(sourceHeight * ratio));
    }

    private static SKRect SourceRectFor(SKBitmap source, int width, int height, ThumbMode mode)
    {
        var targetRatio = width / (double)height;
        var sourceRatio = source.Width / (double)source.Height;

        if (sourceRatio > targetRatio)
        {
            // The source is wider than the target: we crop left and right, always centered — a
            // "top" or "bottom" horizontal crop wouldn't make sense.
            var cropped = (float)(source.Height * targetRatio);
            var left = (source.Width - cropped) / 2f;

            return new SKRect(left, 0, left + cropped, source.Height);
        }

        var croppedHeight = (float)(source.Width / targetRatio);

        var top = mode switch
        {
            ThumbMode.Top => 0f,
            ThumbMode.Bottom => source.Height - croppedHeight,
            _ => (source.Height - croppedHeight) / 2f,
        };

        return new SKRect(0, top, source.Width, top + croppedHeight);
    }
}
