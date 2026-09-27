using FarmEngine.Authoring;
using FarmEngine.Schemas;
using SkiaSharp;

namespace FarmingRpgMaker.App.Game;

/// <summary>Converts supported raster art to a self-contained PNG asset for native projects.</summary>
internal static class ArtImport
{
    internal const int MaxBytes = 16 * 1024 * 1024;
    private const int MaxSide = 8192;
    private const int MaxPixels = 16 * 1024 * 1024;
    private static readonly HashSet<string> Extensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];

    public static CustomAsset FromBytes(GameProject project, string fileName, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(bytes);
        if (!Extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant()))
            throw new ArgumentException("Use PNG, JPEG, WebP, GIF or BMP artwork.", nameof(fileName));
        if (bytes.Length is 0 or > MaxBytes)
            throw new ArgumentException("Each image must be under 16 MB.", nameof(bytes));

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
        var encoded = png.ToArray();
        if (encoded.Length > MaxBytes) throw new ArgumentException("The converted image exceeds 16 MB.", nameof(bytes));
        var id = Defaults.NextId("art", project.CustomAssets.Select(asset => asset.Id));
        return new CustomAsset
        {
            Id = id,
            Name = fileName,
            Type = CustomAssetTypes.Art,
            DataUrl = "data:image/png;base64," + Convert.ToBase64String(encoded),
            Width = width,
            Height = height,
        };
    }
}
