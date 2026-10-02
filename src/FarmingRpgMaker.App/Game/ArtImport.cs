using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using FarmEngine.Authoring;
using FarmEngine.Authoring.Net;
using FarmEngine.Schemas;
using SkiaSharp;

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
        // The header is read in managed code first, so Skia's codecs only see the five accepted
        // formats at a size within the limits (docs/SKIA-MIGRATION.md).
        if (ImageHeaders.Read(bytes) is not { } header)
            throw new ArgumentException("The image could not be decoded: use PNG, JPEG, WebP, GIF or BMP artwork.", nameof(bytes));
        if (!ArtBitmaps.WithinLimits(header.Width, header.Height))
            throw new ArgumentException("Use images up to 8192 pixels per side and 16 megapixels total.", nameof(bytes));

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) throw new ArgumentException("The image could not be decoded.", nameof(bytes));
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (!ArtBitmaps.WithinLimits(width, height))
            throw new ArgumentException("Use images up to 8192 pixels per side and 16 megapixels total.", nameof(bytes));

        using var bitmap = SKBitmap.Decode(codec) ?? throw new ArgumentException("The image could not be decoded.", nameof(bytes));
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
        SKPicture? picture;
        try
        {
            // The same safe reader as the check: DTDs are skipped, and the renderer refuses to
            // load anything that is not embedded in the file (SvgSafety).
            picture = SvgSafety.Render(text, SafeReaderSettings());
        }
        catch (Exception error) when (error is XmlException or InvalidOperationException or ArgumentException or FormatException or NullReferenceException
            or NotSupportedException)
        {
            throw new ArgumentException($"The SVG could not be read: {error.Message}", nameof(bytes), error);
        }

        if (picture is null) throw new ArgumentException("The SVG could not be read.", nameof(bytes));
        using var owned = picture;
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

    internal static XmlReaderSettings SafeReaderSettings() =>
        new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true };

    /// <summary>How deep SVG images may nest inside an imported SVG (as <c>data:</c> URLs).</summary>
    internal const int MaxSvgNesting = 2;

    /// <summary>A DOCTYPE with an internal subset: entity and default-attribute declarations.</summary>
    [GeneratedRegex(@"<!DOCTYPE[^>\[]*\[")]
    private static partial Regex InternalSubset();

    private static ArgumentException External() =>
        new("The SVG refers to an external file; only self-contained SVGs can be imported.", "bytes");

    /// <summary>
    /// Refuses SVGs that could run code or reach outside the file: scripts, event handlers,
    /// foreign HTML, DTD declarations, and links or CSS urls that are not <c>#fragments</c> or
    /// embedded <c>data:</c> images. Embedded images are checked as well: SVG images recursively
    /// (at most <see cref="MaxSvgNesting"/> deep), raster images by their header. DTDs are
    /// skipped and never resolved. Returns the SVG text.
    /// </summary>
    internal static string CheckSvg(byte[] bytes) => CheckSvg(bytes, 0);

    private static string CheckSvg(byte[] bytes, int depth)
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

        // Declarations in the DTD can add attributes (an image link) that only a parser which
        // reads the DTD would see.
        if (InternalSubset().IsMatch(text))
            throw new ArgumentException("The SVG declares XML entities or attributes in its DOCTYPE, which is not supported.", nameof(bytes));

        var settings = SafeReaderSettings();
        var sawSvg = false;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            while (reader.Read())
            {
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA) CheckCss(reader.Value, depth);
                if (reader.NodeType == XmlNodeType.EntityReference)
                    throw new ArgumentException("The SVG uses XML entities, which are not supported.", nameof(bytes));
                // The renderer reads a nested SVG from bytes again, and that reader honours the declaration.
                if (reader.NodeType == XmlNodeType.XmlDeclaration && depth > 0
                    && reader.GetAttribute("encoding") is { } encoding && !encoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("An SVG image inside the SVG must be UTF-8 text.", nameof(bytes));
                if (reader.NodeType != XmlNodeType.Element) continue;
                var name = reader.LocalName;
                if (!sawSvg)
                {
                    if (!name.Equals("svg", StringComparison.Ordinal)) throw new ArgumentException("This file is not an SVG image.", nameof(bytes));
                    sawSvg = true;
                }

                if (name.Equals("script", StringComparison.OrdinalIgnoreCase) || name.Equals("foreignObject", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("iframe", StringComparison.OrdinalIgnoreCase) || name.Equals("handler", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("SVGs with scripts or embedded HTML cannot be imported.", nameof(bytes));
                if (!reader.MoveToFirstAttribute()) continue;
                do
                {
                    var attribute = reader.LocalName;
                    if (attribute.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("SVGs with scripts or embedded HTML cannot be imported.", nameof(bytes));
                    if (attribute is "href" or "src") CheckReference(reader.Value, depth);
                    // xml:base would turn #fragments and relative links into external ones.
                    if (attribute == "base" && reader.NamespaceURI == "http://www.w3.org/XML/1998/namespace") throw External();
                    CheckCss(reader.Value, depth);
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

    /// <summary>Refuses CSS that imports a stylesheet or has a <c>url()</c> that is not local.</summary>
    private static void CheckCss(string css, int depth)
    {
        // CSS escapes spell the same rules and functions (\40import, u\72l(…)).
        if (css.Contains('\\', StringComparison.Ordinal)) css = CssUnescape(css);
        if (css.Contains("@import", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The SVG imports an external stylesheet.", "bytes");
        foreach (var target in CssUrls(css))
        {
            CheckReference(target, depth);
        }
    }

    /// <summary>The target of every <c>url(…)</c> in <paramref name="css"/>, quoted or not.</summary>
    internal static IEnumerable<string> CssUrls(string css)
    {
        var at = 0;
        while ((at = css.IndexOf("url(", at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            at += 4;
            while (at < css.Length && char.IsWhiteSpace(css[at])) at++;
            var quoted = at < css.Length && css[at] is '"' or '\'';
            var end = quoted ? css.IndexOf(css[at], at + 1) : css.IndexOf(')', at);
            if (end < 0) end = css.Length;
            yield return quoted ? css[(at + 1)..end] : css[at..end];
            at = end;
        }
    }

    /// <summary>
    /// Resolves CSS escapes: a backslash and 1–6 hex digits (and one whitespace after them), or a
    /// backslash and any other character (an escaped line break is dropped).
    /// </summary>
    internal static string CssUnescape(string css)
    {
        var text = new StringBuilder(css.Length);
        var at = 0;
        while (at < css.Length)
        {
            var c = css[at++];
            if (c != '\\' || at == css.Length)
            {
                text.Append(c);
                continue;
            }

            var start = at;
            while (at < css.Length && at - start < 6 && char.IsAsciiHexDigit(css[at])) at++;
            if (at == start)
            {
                if (css[at] is not ('\n' or '\r' or '\f')) text.Append(css[at]);
                at++;
                continue;
            }

            var code = int.Parse(css.AsSpan(start, at - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            text.Append(code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(code) : "�");
            if (at < css.Length && char.IsWhiteSpace(css[at])) at++;
        }

        return text.ToString();
    }

    /// <summary>
    /// A link is fine when it is empty, a <c>#fragment</c> or an embedded image that passes
    /// <see cref="CheckEmbeddedImage"/>; the renderer could fetch anything else.
    /// </summary>
    private static void CheckReference(string target, int depth)
    {
        var value = target.Trim();
        if (value.Length == 0 || value.StartsWith('#')) return;
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) throw External();
        CheckEmbeddedImage(value, depth);
    }

    /// <summary>
    /// An embedded image must be a base64 <c>data:image/…</c> URL (with at most a UTF-8 charset
    /// parameter) of a PNG, JPEG, GIF, WebP or BMP image within the import limits, or of an SVG
    /// that passes <see cref="CheckSvg(byte[])"/> itself, which is how the Svg.Skia renderer would
    /// read it. The renderer also reads any gzip payload as an SVG, so compressed payloads are
    /// refused.
    /// </summary>
    private static void CheckEmbeddedImage(string url, int depth)
    {
        var comma = url.IndexOf(',', StringComparison.Ordinal);
        var parameters = url[5..(comma < 0 ? url.Length : comma)].Split(';');
        var mediaType = parameters[0].Trim();
        if (comma < 0 || parameters.Length < 2 || !parameters[^1].Trim().Equals("base64", StringComparison.OrdinalIgnoreCase)
            || parameters[1..^1].Any(parameter => !parameter.Trim().Equals("charset=utf-8", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Images inside the SVG must be base64 data: URLs.", "bytes");
        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(url[(comma + 1)..]);
        }
        catch (FormatException)
        {
            throw new ArgumentException("An image inside the SVG is damaged (invalid base64).", "bytes");
        }

        if (payload.Length >= 2 && payload[0] == 0x1F && payload[1] == 0x8B)
            throw new ArgumentException("Compressed SVG images (svgz) inside the SVG are not supported.", "bytes");
        if (mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            if (depth >= MaxSvgNesting)
                throw new ArgumentException($"SVG images inside the SVG may nest at most {MaxSvgNesting} deep.", "bytes");
            CheckSvg(payload, depth + 1);
            return;
        }

        if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || ImageHeaders.Read(payload) is not { } header)
            throw new ArgumentException("Images inside the SVG must be PNG, JPEG, GIF, WebP, BMP or SVG.", "bytes");
        if (!ArtBitmaps.WithinLimits(header.Width, header.Height))
            throw new ArgumentException("An image inside the SVG is over 8192 pixels per side or 16 megapixels.", "bytes");
    }
}
