using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using SkiaSharp;
using Svg.Skia;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Converts supported art to a self-contained PNG asset for native projects: raster images are
/// re-encoded, SVG is rasterized (web <c>import-art.ts</c> draws both onto a canvas).
/// </summary>
internal static partial class ArtImport
{
    internal const int MaxBytes = 16 * 1024 * 1024;
    internal const int MaxSide = 8192;
    internal const int MaxPixels = 16 * 1024 * 1024;
    /// <summary>SVGs without a size of their own become this wide on their longest side.</summary>
    internal const int DefaultSvgSide = 512;
    private static readonly HashSet<string> RasterExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];
    internal static readonly string[] FilePatterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp", "*.svg"];

    /// <summary>
    /// Imports raster art or an SVG. <paramref name="svgSide"/> is the longest side an SVG is
    /// rasterized to; null keeps the SVG's own size. Either way the result fits the raster limits.
    /// </summary>
    public static CustomAsset FromBytes(GameProject project, string fileName, byte[] bytes, int? svgSide = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(bytes);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!RasterExtensions.Contains(extension) && extension != ".svg")
            throw new ArgumentException("Use PNG, JPEG, WebP, GIF, BMP or SVG artwork.", nameof(fileName));
        if (bytes.Length is 0 or > MaxBytes)
            throw new ArgumentException("Each image must be under 16 MB.", nameof(bytes));
        var (png, width, height) = extension == ".svg" ? RasterizeSvg(bytes, svgSide) : Reencode(bytes);
        if (png.Length > MaxBytes) throw new ArgumentException("The converted image exceeds 16 MB.", nameof(bytes));
        var id = Defaults.NextId("art", project.CustomAssets.Select(asset => asset.Id));
        return CustomAsset.Default.WithId(id).WithName(fileName).WithType(CustomAssetTypes.Art).WithDataUrl("data:image/png;base64," + Convert.ToBase64String(png)).WithWidth(width).WithHeight(height);
    }

    private static (byte[] Png, int Width, int Height) Reencode(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) throw new ArgumentException("The image could not be decoded.", nameof(bytes));
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide || (long)width * height > MaxPixels)
            throw new ArgumentException("Use images up to 8192 pixels per side and 16 megapixels total.", nameof(bytes));

        using var bitmap = SKBitmap.Decode(bytes) ?? throw new ArgumentException("The image could not be decoded.", nameof(bytes));
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new ArgumentException("The image could not be converted to PNG.", nameof(bytes));
        return (png.ToArray(), width, height);
    }

    /// <summary>
    /// The pixel size an SVG of <paramref name="width"/>×<paramref name="height"/> units becomes:
    /// scaled so the longest side is <paramref name="side"/> (or its own size, or
    /// <see cref="DefaultSvgSide"/> when it has none), then shrunk to fit the raster limits.
    /// </summary>
    internal static (int Width, int Height) SvgPixelSize(double width, double height, int? side)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) width = height = DefaultSvgSide;
        var longest = Math.Max(width, height);
        var scale = side is { } wanted ? wanted / longest : 1.0;
        scale = Math.Min(scale, MaxSide / longest);
        scale = Math.Min(scale, Math.Sqrt(MaxPixels / (width * height)));
        // Round down (with a little slack for float error) so the result never exceeds the limits.
        static int Pixels(double units) => Math.Clamp((int)Math.Floor(units + 1e-6), 1, MaxSide);
        return (Pixels(width * scale), Pixels(height * scale));
    }

    private static (byte[] Png, int Width, int Height) RasterizeSvg(byte[] bytes, int? side)
    {
        if (side is <= 0 or > MaxSide) throw new ArgumentException("The SVG size must be between 1 and 8192 pixels.", nameof(side));
        var text = CheckSvg(bytes);
        using var svg = new SKSvg();
        SKPicture? picture;
        try
        {
            // The same safe reader as the check: DTDs are skipped and nothing is fetched.
            using var reader = XmlReader.Create(new StringReader(text), SafeReaderSettings());
            picture = svg.Load(reader);
        }
        catch (Exception error) when (error is XmlException or InvalidOperationException or ArgumentException or FormatException or NullReferenceException)
        {
            throw new ArgumentException($"The SVG could not be read: {error.Message}", nameof(bytes), error);
        }

        if (picture is null) throw new ArgumentException("The SVG could not be read.", nameof(bytes));
        var bounds = picture.CullRect;
        var (width, height) = SvgPixelSize(bounds.Width, bounds.Height, side);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                canvas.Scale(width / bounds.Width, height / bounds.Height);
                canvas.Translate(-bounds.Left, -bounds.Top);
            }

            canvas.DrawPicture(picture);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new ArgumentException("The SVG could not be converted to PNG.", nameof(bytes));
        return (png.ToArray(), width, height);
    }

    private static XmlReaderSettings SafeReaderSettings() =>
        new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true };

    [GeneratedRegex(@"url\(\s*(['""]?)(?<target>[^)'""]*)\1\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrl();

    /// <summary>
    /// Refuses SVGs that could run code or reach outside the file: scripts, event handlers,
    /// foreign HTML, and links or CSS urls that are not <c>#fragments</c> or <c>data:</c> images.
    /// DTDs are skipped and never resolved. Returns the SVG text.
    /// </summary>
    internal static string CheckSvg(byte[] bytes)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new ArgumentException("The SVG must be UTF-8 text.", nameof(bytes));
        }

        static bool LocalReference(string target)
        {
            var value = target.Trim();
            return value.Length == 0 || value.StartsWith('#')
                || value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
        }

        void CheckCss(string css)
        {
            if (css.Contains("@import", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The SVG imports an external stylesheet.", nameof(bytes));
            foreach (Match match in CssUrl().Matches(css))
            {
                if (!LocalReference(match.Groups["target"].Value))
                    throw new ArgumentException("The SVG refers to an external file; only self-contained SVGs can be imported.", nameof(bytes));
            }
        }

        var settings = SafeReaderSettings();
        var sawSvg = false;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            while (reader.Read())
            {
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA) CheckCss(reader.Value);
                if (reader.NodeType == XmlNodeType.EntityReference)
                    throw new ArgumentException("The SVG uses XML entities, which are not supported.", nameof(bytes));
                if (reader.NodeType != XmlNodeType.Element) continue;
                var name = reader.LocalName;
                if (!sawSvg)
                {
                    if (!name.Equals("svg", StringComparison.Ordinal)) throw new ArgumentException("This file is not an SVG image.", nameof(bytes));
                    sawSvg = true;
                }

                if (name.Equals("script", StringComparison.OrdinalIgnoreCase) || name.Equals("foreignObject", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("iframe", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("SVGs with scripts or embedded HTML cannot be imported.", nameof(bytes));
                if (!reader.MoveToFirstAttribute()) continue;
                do
                {
                    var attribute = reader.LocalName;
                    if (attribute.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("SVGs with scripts or embedded HTML cannot be imported.", nameof(bytes));
                    if ((attribute is "href" or "src") && !LocalReference(reader.Value))
                        throw new ArgumentException("The SVG refers to an external file; only self-contained SVGs can be imported.", nameof(bytes));
                    CheckCss(reader.Value);
                } while (reader.MoveToNextAttribute());
                reader.MoveToElement();
            }
        }
        catch (XmlException error)
        {
            throw new ArgumentException($"The SVG is not valid XML: {error.Message}", nameof(bytes), error);
        }

        if (!sawSvg) throw new ArgumentException("This file is not an SVG image.", nameof(bytes));
        return text;
    }
}
