using System.Net;
using System.Xml;
using Svg;
using Svg.Model;
using Svg.Model.Services;
using Svg.Skia;
using Shim = ShimSkiaSharp;

namespace FarmingRpgMaker.App.Game;

/// <summary>
/// Renders SVGs that <see cref="ArtImport.CheckSvg"/> accepted, with every way Svg.Skia could
/// reach outside the file switched off as well (defence in depth: the check is the first line):
/// <list type="bullet">
/// <item>The SVG library never resolves external elements (<c>&lt;use&gt;</c>, <c>url()</c>),
/// images or XML entities, and SVG documents nested in <c>data:</c> URLs skip their DTD.</item>
/// <item>Every <c>&lt;image&gt;</c> of the parsed document must link to a <c>data:</c> URL or a
/// <c>#fragment</c>.</item>
/// <item><see cref="WebRequest"/>, which Svg.Model uses to load images that are not
/// <c>data:</c> URLs (including from SVGs nested in them), refuses every http, https, ftp and
/// file URL in this process. Nothing else in the editor uses it (HttpClient is separate).</item>
/// <item>Embedded raster images are decoded only when their header is a PNG, JPEG, GIF, WebP or
/// BMP image within the import limits (<see cref="ImageHeaders"/>).</item>
/// </list>
/// </summary>
internal static class SvgSafety
{
    private static readonly Lock Gate = new();
    private static bool _configured;

    /// <summary>The URL prefixes <see cref="WebRequest"/> refuses once SVG import has run.</summary>
    internal static readonly string[] RefusedPrefixes = ["http://", "https://", "ftp://", "file://"];

    /// <summary>Switches off the SVG library's external loading for this process (idempotent).</summary>
    public static void EnsureConfigured()
    {
        lock (Gate)
        {
            if (_configured)
            {
                return;
            }

            SvgDocument.DisableDtdProcessing = true;
            SvgDocument.ResolveExternalXmlEntites = ExternalType.None;
            SvgDocument.ResolveExternalImages = ExternalType.None;
            SvgDocument.ResolveExternalElements = ExternalType.None;
            // The longest registered prefix wins, so these take over from the built-in "http:"…
            // creators for every absolute URL of those schemes.
#pragma warning disable SYSLIB0014 // WebRequest is obsolete; Svg.Model still calls it.
            foreach (var prefix in RefusedPrefixes)
            {
                WebRequest.RegisterPrefix(prefix, RefusingRequests.Instance);
            }
#pragma warning restore SYSLIB0014
            _configured = true;
        }
    }

    /// <summary>
    /// Parses the SVG text with <paramref name="settings"/> and records it as a Skia picture,
    /// or null when it has nothing to draw. Throws <see cref="ArgumentException"/> when an image
    /// links outside the file or an embedded image is refused.
    /// </summary>
    public static SkiaSharp.SKPicture? Render(string text, XmlReaderSettings settings)
    {
        EnsureConfigured();
        using var reader = XmlReader.Create(new StringReader(text), settings);
        if (SvgService.Open(reader) is not { } document)
        {
            return null;
        }

        CheckImageLinks(document);
        var model = new SkiaModel(new SKSvgSettings());
        return SKSvg.ToPicture(document, model, new CheckedAssetLoader(model));
    }

    /// <summary>Refuses images that link to anything but a <c>data:</c> URL or a <c>#fragment</c>.</summary>
    internal static void CheckImageLinks(SvgElement element)
    {
        var href = element switch
        {
            Svg.SvgImage image => image.Href,
            Svg.FilterEffects.SvgImage image => image.Href,
            _ => null,
        };
        var target = href?.Trim() ?? "";
        if (target.Length > 0 && !target.StartsWith('#') && !target.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The SVG refers to an external file; only self-contained SVGs can be imported.");
        }

        foreach (var child in element.Children)
        {
            CheckImageLinks(child);
        }
    }

#pragma warning disable SYSLIB0014
    private sealed class RefusingRequests : IWebRequestCreate
    {
        public static readonly RefusingRequests Instance = new();

        public WebRequest Create(Uri uri) => throw new NotSupportedException($"The editor does not load {uri.Scheme} resources for SVG images.");
    }
#pragma warning restore SYSLIB0014

    /// <summary>Svg.Skia's loader, but embedded images must pass the header check first.</summary>
    private sealed class CheckedAssetLoader(SkiaModel model) : ISvgAssetLoader
    {
        private readonly SkiaSvgAssetLoader _inner = new(model);

        public Shim.SKImage LoadImage(Stream stream)
        {
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            if (ImageHeaders.Read(bytes) is not { } header || !ArtBitmaps.WithinLimits(header.Width, header.Height))
            {
                throw new ArgumentException("An image inside the SVG is not a PNG, JPEG, GIF, WebP or BMP image within the import limits.");
            }

            return _inner.LoadImage(new MemoryStream(bytes, writable: false));
        }

        public List<TypefaceSpan> FindTypefaces(string? text, Shim.SKPaint paintPreferredTypeface) => _inner.FindTypefaces(text, paintPreferredTypeface);

        public Shim.SKFontMetrics GetFontMetrics(Shim.SKPaint paint) => _inner.GetFontMetrics(paint);

        public float MeasureText(string? text, Shim.SKPaint paint, ref Shim.SKRect bounds) => _inner.MeasureText(text, paint, ref bounds);

        public Shim.SKPath? GetTextPath(string? text, Shim.SKPaint paint, float x, float y) => _inner.GetTextPath(text, paint, x, y);
    }
}
