using System.Globalization;
using SkiaSharp;

namespace Cratebase.Storage;

/// <summary>Taille de vignette demandée.</summary>
/// <param name="Width">Largeur, ou 0 pour « déduire du ratio ».</param>
/// <param name="Height">Hauteur, ou 0 pour « déduire du ratio ».</param>
/// <param name="Mode">Mode de cadrage.</param>
public readonly record struct ThumbSize(int Width, int Height, ThumbMode Mode)
{
    /// <summary>Forme canonique, telle qu'elle sert de clé de cache.</summary>
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

/// <summary>Mode de cadrage d'une vignette.</summary>
public enum ThumbMode
{
    /// <summary>Recadrage centré.</summary>
    Crop,

    /// <summary>Recadrage sur le haut.</summary>
    Top,

    /// <summary>Recadrage sur le bas.</summary>
    Bottom,

    /// <summary>Contenu entier, sans recadrage.</summary>
    Fit,
}

/// <summary>
/// Génération de vignettes.
/// </summary>
/// <remarks>
/// Syntaxe reprise de PocketBase : <c>WxH</c>, <c>WxHt</c>, <c>WxHb</c>, <c>WxHf</c>, <c>0xH</c>,
/// <c>Wx0</c>. Un client écrit contre PocketBase n'a donc rien à réapprendre.
/// </remarks>
public static class ThumbnailGenerator
{
    /// <summary>Dimension maximale d'une vignette, en pixels.</summary>
    /// <remarks>
    /// Borne obligatoire : sans elle, <c>?thumb=20000x20000</c> demande au serveur d'allouer plus
    /// d'un gigaoctet, en une URL et sans authentification si le fichier est public.
    /// </remarks>
    public const int MaxDimension = 4000;

    /// <summary>Formats d'entrée pris en charge.</summary>
    public static bool IsSupported(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

    /// <summary>Analyse une expression de taille.</summary>
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
    /// Produit une vignette au format PNG.
    /// </summary>
    /// <returns>Les octets de la vignette, ou <see langword="null"/> si la source est illisible.</returns>
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
        // Une dimension à zéro signifie « déduire du ratio » : c'est la forme la plus utile côté
        // client, qui connaît sa largeur d'affichage mais pas la hauteur de l'image.
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
            // La source est plus large que la cible : on rogne à gauche et à droite, toujours au
            // centre — un rognage horizontal « haut » ou « bas » n'aurait pas de sens.
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
