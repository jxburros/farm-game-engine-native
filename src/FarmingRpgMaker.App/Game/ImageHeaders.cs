using System.Buffers.Binary;

namespace FarmingRpgMaker.App.Game;

/// <summary>The raster formats art import accepts (<see cref="ArtImport"/>).</summary>
internal enum RasterFormat
{
    Png,
    Jpeg,
    Gif,
    WebP,
    Bmp,
}

/// <summary>
/// Reads the format and pixel size from the first bytes of an image, in managed code. Every
/// decode of image data the editor did not make itself (imported art, project and pack art,
/// images embedded in SVGs) checks this first, so SkiaSharp's native codecs only ever see one of
/// the five accepted formats with a size inside the import limits (docs/SKIA-MIGRATION.md).
/// </summary>
internal static class ImageHeaders
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10];

    /// <summary>
    /// The format and the width and height the header declares, or null when the bytes do not
    /// start like a PNG, JPEG, GIF, WebP or BMP image or the header is cut short.
    /// </summary>
    public static (RasterFormat Format, int Width, int Height)? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(PngSignature))
        {
            // The IHDR chunk comes first: length 13, "IHDR", width, height (big-endian).
            return bytes.Length >= 24 && bytes.Slice(12, 4).SequenceEqual("IHDR"u8)
                ? Sized(RasterFormat.Png, BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]))
                : null;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return Jpeg(bytes);
        }

        if (bytes.Length >= 10 && (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)))
        {
            return Sized(RasterFormat.Gif, BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]));
        }

        if (bytes.Length >= 16 && bytes.StartsWith("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return WebP(bytes);
        }

        if (bytes.Length >= 26 && bytes.StartsWith("BM"u8))
        {
            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
            if (headerSize == 12)
            {
                return Sized(RasterFormat.Bmp, BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..]));
            }

            // Negative heights mean top-down rows.
            var height = BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]);
            return headerSize >= 40
                ? Sized(RasterFormat.Bmp, BinaryPrimitives.ReadUInt32LittleEndian(bytes[18..]), height == int.MinValue ? uint.MaxValue : (uint)Math.Abs(height))
                : null;
        }

        return null;
    }

    private static (RasterFormat, int, int)? Sized(RasterFormat format, uint width, uint height) =>
        (format, (int)Math.Min(width, int.MaxValue), (int)Math.Min(height, int.MaxValue));

    /// <summary>The size in the first start-of-frame segment.</summary>
    private static (RasterFormat, int, int)? Jpeg(ReadOnlySpan<byte> bytes)
    {
        var at = 2;
        while (at + 4 <= bytes.Length)
        {
            if (bytes[at] != 0xFF)
            {
                return null;
            }

            var marker = bytes[at + 1];
            if (marker == 0xFF)
            {
                at++; // fill byte
                continue;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                at += 2; // markers without a length
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                return null; // end of image or start of scan before any frame header
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 2)..]);
            // SOF0–SOF15 except DHT (C4), JPG (C8) and DAC (CC): precision, height, width.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return at + 9 <= bytes.Length
                    ? Sized(RasterFormat.Jpeg, BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(bytes[(at + 5)..]))
                    : null;
            }

            if (length < 2)
            {
                return null;
            }

            at += 2 + length;
        }

        return null;
    }

    /// <summary>The canvas size of a lossy (VP8), lossless (VP8L) or extended (VP8X) WebP.</summary>
    private static (RasterFormat, int, int)? WebP(ReadOnlySpan<byte> bytes)
    {
        var chunk = bytes.Slice(12, 4);
        if (chunk.SequenceEqual("VP8 "u8) && bytes.Length >= 30 && bytes[23] == 0x9D && bytes[24] == 0x01 && bytes[25] == 0x2A)
        {
            return Sized(RasterFormat.WebP, BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3FFFu, BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3FFFu);
        }

        if (chunk.SequenceEqual("VP8L"u8) && bytes.Length >= 25 && bytes[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]);
            return Sized(RasterFormat.WebP, (bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
        }

        if (chunk.SequenceEqual("VP8X"u8) && bytes.Length >= 30)
        {
            static uint Uint24(ReadOnlySpan<byte> b) => b[0] | ((uint)b[1] << 8) | ((uint)b[2] << 16);
            return Sized(RasterFormat.WebP, Uint24(bytes[24..]) + 1, Uint24(bytes[27..]) + 1);
        }

        return null;
    }
}
