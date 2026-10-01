namespace FarmEngine.Export

open System
open System.IO
open SkiaSharp

/// Game icons: the creator's PNG asset (export setting `icon`) or the default icon, rendered
/// with Skia at the sizes Windows and Linux launchers use.
module Icons =
    /// Windows icon sizes; Linux uses the 256×256 image.
    let sizes = [ 16; 32; 48; 256 ]

    /// The bytes of a `data:image/png;base64,…` URL.
    let decodeDataUrl (url: string) : Result<byte[], string> =
        let comma = url.IndexOf ','
        if not (url.StartsWith("data:image/png", StringComparison.OrdinalIgnoreCase)) || comma < 0 then
            Error "The icon asset is not a PNG image."
        elif not (url.Substring(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) then
            Error "The icon asset is not base64 data."
        else
            try
                Ok(Convert.FromBase64String(url.Substring(comma + 1)))
            with :? FormatException ->
                Error "The icon asset's image data is damaged."

    /// The default game icon (the editor's icon, as engines usually do).
    let defaultIcon () : byte[] =
        use stream =
            match typeof<PackageFile>.Assembly.GetManifestResourceStream "default-icon.png" with
            | null -> invalidOp "The default icon resource is missing."
            | stream -> stream
        use memory = new MemoryStream()
        stream.CopyTo memory
        memory.ToArray()

    /// `png` scaled into a transparent `size`×`size` square (centred, aspect kept), as PNG.
    let private renderOne (source: SKBitmap) (size: int) : byte[] =
        let info = SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul)
        use surface = SKSurface.Create info
        let canvas = surface.Canvas
        canvas.Clear SKColors.Transparent
        let scale = min (float32 size / float32 source.Width) (float32 size / float32 source.Height)
        let width = float32 source.Width * scale
        let height = float32 source.Height * scale
        let target = SKRect.Create((float32 size - width) / 2.0f, (float32 size - height) / 2.0f, width, height)
        use paint = new SKPaint(IsAntialias = true, FilterQuality = SKFilterQuality.High)
        canvas.DrawBitmap(source, target, paint)
        canvas.Flush()
        use image = surface.Snapshot()
        use data = image.Encode(SKEncodedImageFormat.Png, 100)
        data.ToArray()

    /// The largest side an icon image may have (the art import limit).
    let MaxSide = 8192

    /// The most pixels an icon image may have (the art import limit: 16 megapixels).
    let MaxPixels = 16L * 1024L * 1024L

    /// The icon at every size in `sizes`, from PNG (or any format Skia decodes) bytes. The size
    /// in the image header is checked against the art import limits before anything is decoded,
    /// so a small file claiming a huge image (a decompression bomb) is refused cheaply.
    let render (png: byte[]) : Result<(int * byte[]) list, string> =
        use data = SKData.CreateCopy png
        use codec = SKCodec.Create data
        match codec with
        | null -> Error "The icon image could not be decoded."
        | codec when
            codec.Info.Width > MaxSide
            || codec.Info.Height > MaxSide
            || int64 codec.Info.Width * int64 codec.Info.Height > MaxPixels
            ->
            Error(
                sprintf
                    "The icon image is %d×%d pixels; use images up to %d pixels per side and 16 megapixels in total."
                    codec.Info.Width
                    codec.Info.Height
                    MaxSide
            )
        | codec ->
            use source = SKBitmap.Decode codec
            match source with
            | null -> Error "The icon image could not be decoded."
            | source when source.Width < 1 || source.Height < 1 -> Error "The icon image is empty."
            | source -> Ok [ for size in sizes -> size, renderOne source size ]
