using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FarmEngine.Authoring;
using FarmingRpgMaker.App.Game;
using SkiaSharp;

namespace FarmingRpgMaker.App.Tests.Game;

/// <summary>
/// SVG import never reaches outside the file (#74): the sanitizer follows images nested in
/// <c>data:</c> URLs, CSS escapes and DTD tricks, and the renderer under it refuses to fetch
/// anything even when handed an SVG the sanitizer never saw. Embedded and imported raster
/// images reach Skia only as one of five formats within the size limits (#114).
/// </summary>
public sealed class SvgImportSafetyTests
{
    private const string Ns = """xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" """;

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string SvgDataUrl(string svg) => "data:image/svg+xml;base64," + Convert.ToBase64String(Utf8(svg));

    private static string PngDataUrl(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);

    private static byte[] Png(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static byte[] Encode(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.SeaGreen);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 90);
        return encoded.ToArray();
    }

    /// <summary>The issue's payload: an outer image whose data: URL is an SVG with external images.</summary>
    private static string Nested(string target) => $"""
        <svg {Ns}width="8" height="8">
          <image width="8" height="8" href="{SvgDataUrl($"""<svg {Ns}width="8" height="8"><image width="8" height="8" href="{target}"/><image width="8" height="8" xlink:href="{target}"/></svg>""")}"/>
        </svg>
        """;

    /// <summary>
    /// A loopback listener that records whether anything connected to it. It hangs up on every
    /// connection at once, so a request that does get through fails fast instead of waiting.
    /// </summary>
    private sealed class Probe : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private int _contacts;

        public Probe()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public string Url(string path) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{path}";

        public bool Contacted => Volatile.Read(ref _contacts) > 0 || _listener.Pending();

        public void Dispose() => _listener.Stop();

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref _contacts);
                }
            }
            catch (Exception error) when (error is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // Stopped.
            }
        }
    }

    public static TheoryData<string> ExternalTargets()
    {
        var file = new Uri(Path.Combine(Path.GetTempPath(), "frm-svg-probe.png")).AbsoluteUri;
        return new TheoryData<string> { "http://127.0.0.1:9/nested-image.png", "https://example.com/a.png", file, @"\\host\share\x.png", "other.png", "//example.com/x.png" };
    }

    [Theory]
    [MemberData(nameof(ExternalTargets))]
    public void NestedSvgImagesWithExternalLinksAreRefused(string target)
    {
        var project = ProjectCatalog.CreateInitialProject(0);
        var error = Assert.Throws<ArgumentException>(() => ArtImport.FromBytes(project, "nested.svg", Utf8(Nested(target))));
        Assert.Contains("external", error.Message, StringComparison.Ordinal);

        // Two levels down, and in a CSS url() of the nested SVG.
        var twice = $"""<svg {Ns}><image width="4" height="4" href="{SvgDataUrl(Nested(target))}"/></svg>""";
        Assert.Contains("external", Assert.Throws<ArgumentException>(() => ArtImport.CheckSvg(Utf8(twice))).Message, StringComparison.Ordinal);
        var css = $"""<svg {Ns}><image width="4" height="4" href="{SvgDataUrl($"""<svg {Ns}><rect style="fill:url({target})" width="4" height="4"/></svg>""")}"/></svg>""";
        Assert.Contains("external", Assert.Throws<ArgumentException>(() => ArtImport.CheckSvg(Utf8(css))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportingTheIssuePayloadMakesNoRequest()
    {
        using var probe = new Probe();
        var project = ProjectCatalog.CreateInitialProject(0);
        Assert.Throws<ArgumentException>(() => ArtImport.FromBytes(project, "nested.svg", Utf8(Nested(probe.Url("nested-image.png")))));
        Assert.False(probe.Contacted, "the import connected to the probe");
    }

    [Theory]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><rect style="fill:u\72l(http://example.com/a.svg#g)" width="1" height="1"/></svg>""", "external")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><style>@\69mport 'https://example.com/a.css';</style></svg>""", "external stylesheet")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><rect style="fill:url('https://example.com/a)b.svg#g')" width="1" height="1"/></svg>""", "external")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><rect fill="URL( &quot;file:///etc/passwd&quot; )" width="1" height="1"/></svg>""", "external")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" xml:base="https://example.com/"><use href="#a"/></svg>""", "external")]
    [InlineData("""<!DOCTYPE svg [<!ATTLIST image href CDATA "https://example.com/a.png">]><svg xmlns="http://www.w3.org/2000/svg"><image width="1" height="1"/></svg>""", "DOCTYPE")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><image width="1" height="1" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg'/%3E"/></svg>""", "base64")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><image width="1" height="1" href="data:text/html;base64,PGgxPmhpPC9oMT4="/></svg>""", "PNG, JPEG")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><image width="1" height="1" href="data:image/png;base64,PGgxPmhpPC9oMT4="/></svg>""", "PNG, JPEG")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><image width="1" height="1" href="data:image/png;base64,***"/></svg>""", "base64")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><image width="1" height="1" href="data:image/svg+xml;charset=utf-16;base64,PHN2Zy8+"/></svg>""", "base64")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><handler>alert(1)</handler></svg>""", "scripts")]
    public void SvgTricksAreRefused(string svg, string message)
    {
        var error = Assert.Throws<ArgumentException>(() => ArtImport.CheckSvg(Utf8(svg)));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedPayloadsAreCheckedLikeImports()
    {
        static ArgumentException Refused(string href) =>
            Assert.Throws<ArgumentException>(() => ArtImport.CheckSvg(Utf8($"""<svg {Ns}><image width="4" height="4" href="{href}"/></svg>""")));

        // Svg.Skia reads any gzip payload as an SVG, whatever the media type says.
        using var gzipped = new MemoryStream();
        using (var gzip = new GZipStream(gzipped, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Utf8($"""<svg {Ns}><image href="http://example.com/a.png"/></svg>"""));
        }

        Assert.Contains("svgz", Refused("data:image/png;base64," + Convert.ToBase64String(gzipped.ToArray())).Message, StringComparison.Ordinal);
        Assert.Contains("8192", Refused(PngDataUrl(ImportSafetyTests.BombPng(60000, 60000))).Message, StringComparison.Ordinal);

        // At most two SVG documents deep.
        var inner = $"""<svg {Ns}><rect width="1" height="1"/></svg>""";
        var three = SvgDataUrl($"""<svg {Ns}><image width="4" height="4" href="{SvgDataUrl($"""<svg {Ns}><image width="4" height="4" href="{SvgDataUrl(inner)}"/></svg>""")}"/></svg>""");
        Assert.Contains("nest", Refused(three).Message, StringComparison.Ordinal);
        ArtImport.CheckSvg(Utf8($"""<svg {Ns}><image width="4" height="4" href="{SvgDataUrl($"""<svg {Ns}><image width="4" height="4" href="{SvgDataUrl(inner)}"/></svg>""")}"/></svg>"""));
    }

    [Fact]
    public void SelfContainedSvgsWithEmbeddedImagesStillImport()
    {
        var project = ProjectCatalog.CreateInitialProject(0);
        var nested = SvgDataUrl($"""<svg {Ns}width="4" height="4"><rect width="4" height="4" fill="#00ff00"/></svg>""");
        var svg = $"""
            <svg {Ns}width="8" height="4">
              <image x="0" y="0" width="4" height="4" xlink:href="{PngDataUrl(Png(4, 4, SKColors.Red))}"/>
              <image x="4" y="0" width="4" height="4" href="{nested}"/>
            </svg>
            """;
        var asset = ArtImport.FromBytes(project, "embedded.svg", Utf8(svg));
        using var decoded = SKBitmap.Decode(ArtBitmaps.Bytes(asset.DataUrl)!);
        Assert.Equal((8, 4), (decoded.Width, decoded.Height));
        var left = decoded.GetPixel(1, 2);
        var right = decoded.GetPixel(6, 2);
        Assert.True(left.Red > 200 && left.Green < 60, $"left pixel {left}");
        Assert.True(right.Green > 200 && right.Red < 60, $"right pixel {right}");
    }

    /// <summary>
    /// The renderer on its own, without the sanitizer: images nested in data: URLs, external
    /// &lt;use&gt; and paint links all stay unfetched, and outer image links are refused.
    /// </summary>
    [Fact]
    public void TheRendererFetchesNothingEvenWithoutTheCheck()
    {
        using var probe = new Probe();
        var secret = Path.Combine(Path.GetTempPath(), "frm-svg-secret-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(secret, Png(8, 8, SKColors.Red));
        try
        {
            var fileUrl = new Uri(secret).AbsoluteUri;
            var svg = $"""
                <svg {Ns}width="8" height="8">
                  <image width="8" height="8" href="{SvgDataUrl($"""<svg {Ns}width="8" height="8"><image width="8" height="8" href="{probe.Url("nested-image.png")}"/><image width="8" height="8" xlink:href="{fileUrl}"/></svg>""")}"/>
                  <use href="{probe.Url("use.svg")}#a"/>
                  <rect width="8" height="8" fill="url({probe.Url("paint.svg")}#g)"/>
                </svg>
                """;
            using (var picture = SvgSafety.Render(svg, ArtImport.SafeReaderSettings()))
            {
                Assert.NotNull(picture);
                using var bitmap = new SKBitmap(8, 8);
                using (var canvas = new SKCanvas(bitmap))
                {
                    canvas.Clear(SKColors.Transparent);
                    canvas.DrawPicture(picture);
                }

                Assert.NotEqual(SKColors.Red, bitmap.GetPixel(4, 4));
            }

            Assert.False(probe.Contacted, "rendering connected to the probe");
            foreach (var outer in new[] { probe.Url("outer.png"), fileUrl })
            {
                Assert.Throws<ArgumentException>(() => SvgSafety.Render($"""<svg {Ns}><image width="8" height="8" href="{outer}"/></svg>""", ArtImport.SafeReaderSettings()));
            }

            Assert.False(probe.Contacted);
#pragma warning disable SYSLIB0014
            Assert.Throws<NotSupportedException>(() => WebRequest.Create(probe.Url("direct.png")));
            Assert.Throws<NotSupportedException>(() => WebRequest.Create(new Uri(fileUrl)));
#pragma warning restore SYSLIB0014
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public void ImageHeadersAreReadWithoutSkia()
    {
        Assert.Equal((RasterFormat.Png, 12, 7), ImageHeaders.Read(Encode(SKEncodedImageFormat.Png, 12, 7)));
        Assert.Equal((RasterFormat.Jpeg, 12, 7), ImageHeaders.Read(Encode(SKEncodedImageFormat.Jpeg, 12, 7)));
        Assert.Equal((RasterFormat.WebP, 12, 7), ImageHeaders.Read(Encode(SKEncodedImageFormat.Webp, 12, 7)));
        Assert.Equal((RasterFormat.Png, 60000, 60000), ImageHeaders.Read(ImportSafetyTests.BombPng(60000, 60000)));

        byte[] gif = [.. "GIF89a"u8, 0x30, 0x75, 0x30, 0x75, 0, 0, 0];
        Assert.Equal((RasterFormat.Gif, 30000, 30000), ImageHeaders.Read(gif));
        var bmp = new byte[54];
        "BM"u8.CopyTo(bmp);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 9000);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -3);
        Assert.Equal((RasterFormat.Bmp, 9000, 3), ImageHeaders.Read(bmp));

        // ICO, truncated and unknown data have no accepted header.
        Assert.Null(ImageHeaders.Read([0, 0, 1, 0, 1, 0, 16, 16]));
        Assert.Null(ImageHeaders.Read(Encode(SKEncodedImageFormat.Png, 4, 4).AsSpan(0, 20)));
        Assert.Null(ImageHeaders.Read("<svg/>"u8));
        Assert.Null(ImageHeaders.Read([0xFF, 0xD8, 0xFF, 0xD9]));
    }

    [Fact]
    public void ImportedRasterArtMustBeAnAcceptedFormatWithinTheLimits()
    {
        var project = ProjectCatalog.CreateInitialProject(0);
        foreach (var format in new[] { SKEncodedImageFormat.Png, SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Webp })
        {
            var asset = ArtImport.FromBytes(project, "art.png", Encode(format, 12, 7));
            Assert.Equal((12.0, 7.0), (asset.Width!.Value, asset.Height!.Value));
        }

        var ico = Assert.Throws<ArgumentException>(() => ArtImport.FromBytes(project, "art.bmp", [0, 0, 1, 0, 1, 0, 16, 16, 0, 0, 1, 0, 32, 0]));
        Assert.Contains("PNG, JPEG", ico.Message, StringComparison.Ordinal);
        byte[] bigGif = [.. "GIF89a"u8, 0x30, 0x75, 0x30, 0x75, 0, 0, 0];
        Assert.Contains("8192", Assert.Throws<ArgumentException>(() => ArtImport.FromBytes(project, "art.gif", bigGif)).Message, StringComparison.Ordinal);
        Assert.Null(ArtBitmaps.HeaderSize([0, 0, 1, 0, 1, 0, 16, 16]));
        Assert.Equal((30000, 30000), ArtBitmaps.HeaderSize(bigGif));
    }

    [Theory]
    [InlineData(@"u\72l(", "url(")]
    [InlineData(@"\40 import", "@import")]
    [InlineData("a\\\nb", "ab")]
    [InlineData(@"\\", @"\")]
    [InlineData(@"\0", "\uFFFD")]
    public void CssEscapesAreResolvedBeforeChecking(string css, string plain) => Assert.Equal(plain, ArtImport.CssUnescape(css));

    [Fact]
    public void CssUrlsAreFoundQuotedOrNot() =>
        Assert.Equal(["#a", "data:x ", "b)c", " d"], ArtImport.CssUrls("fill:url(#a);x:URL( data:x );y:url('b)c');z:url(\" d\")"));
}
