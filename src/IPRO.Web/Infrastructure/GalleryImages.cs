using SkiaSharp;

namespace IPRO.Web.Infrastructure;

// 535 (2026-09-30): what the gallery keeps of a photo. A phone photo is 3-5 MB at 4000 px and carries
// its EXIF block, GPS position included; the gallery used to store and serve that file as it came, so
// every tile of a 20-photo grid downloaded an original. Now an upload becomes two files:
//
//   - the full view (the lightbox and the slideshow): the long edge at most 1600 px;
//   - the tile (grid, masonry, carousel): the long edge at most 800 px, sharp on a 2x screen.
//
// Both are re-encoded, so no EXIF (and no location) survives; the camera's orientation flag is applied
// first, so a portrait photo stays upright. Opaque images become JPEG (quality 82); an image with real
// transparency stays PNG, since a JPEG would paint its see-through parts black. An animated GIF or WebP
// is kept exactly as uploaded (there is nothing to resize without losing the animation) and has no tile.
// The original is never stored. JPEG or PNG, not WebP: an adviser can put a gallery photo in a
// newsletter, and Outlook on Windows still does not show WebP.
public static class GalleryImages
{
    public const int FullEdge = 1600;
    public const int TileEdge = 800;
    public const int JpegQuality = 82;

    public sealed record Rendition(byte[] Bytes, string ContentType, string Extension, int Width, int Height);

    // Tile is null when the full view already serves as one (a small photo, or an animation kept as it came).
    // SourceWidth x SourceHeight is the upload's own size, upright.
    public sealed record Photo(Rendition Full, Rendition? Tile, int SourceWidth, int SourceHeight)
    {
        public long TotalBytes => Full.Bytes.LongLength + (Tile?.Bytes.LongLength ?? 0);
    }

    // Null when the bytes are not an image this can read; the caller refuses the upload.
    public static Photo? Process(byte[] original)
    {
        if (original.Length == 0) return null;
        using var data = SKData.CreateCopy(original);
        using var codec = SKCodec.Create(data);
        if (codec == null) return null;

        var sourceWidth = codec.Info.Width;
        var sourceHeight = codec.Info.Height;
        if (sourceWidth <= 0 || sourceHeight <= 0) return null;

        if (codec.FrameCount > 1)
        {
            var (animatedType, animatedExtension) = codec.EncodedFormat switch
            {
                SKEncodedImageFormat.Gif => ("image/gif", ".gif"),
                SKEncodedImageFormat.Webp => ("image/webp", ".webp"),
                _ => (string.Empty, string.Empty)
            };
            if (animatedType.Length == 0) return null;
            return new Photo(new Rendition(original, animatedType, animatedExtension, sourceWidth, sourceHeight), null, sourceWidth, sourceHeight);
        }

        var origin = codec.EncodedOrigin;
        var swap = SwapsAxes(origin);
        var orientedWidth = swap ? sourceHeight : sourceWidth;
        var orientedHeight = swap ? sourceWidth : sourceHeight;

        var (fullWidth, fullHeight) = Fit(orientedWidth, orientedHeight, FullEdge);
        using var decoded = Decode(codec, swap ? fullHeight : fullWidth, swap ? fullWidth : fullHeight);
        if (decoded == null) return null;

        var transparent = codec.Info.AlphaType != SKAlphaType.Opaque && HasTransparency(decoded);
        var full = Render(decoded, origin, fullWidth, fullHeight, transparent);

        // A photo already no bigger than a tile is one file: the tile falls back to the full view.
        var (tileWidth, tileHeight) = Fit(orientedWidth, orientedHeight, TileEdge);
        var tile = tileWidth == fullWidth && tileHeight == fullHeight
            ? null
            : Render(decoded, origin, tileWidth, tileHeight, transparent);
        return new Photo(full, tile, orientedWidth, orientedHeight);
    }

    // For /health/imaging: SkiaSharp is native code, and a server that cannot load it would otherwise
    // only show as a failed upload. Null when it works, else what went wrong.
    public static string? SelfCheck()
    {
        try
        {
            using var bitmap = new SKBitmap(new SKImageInfo(64, 48, SKColorType.Rgba8888, SKAlphaType.Opaque));
            bitmap.Erase(new SKColor(20, 87, 217));
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            var photo = Process(encoded.ToArray());
            return photo?.Full.Width == 64 && photo.Full.Height == 48 ? null : "the test image did not come back at 64 x 48";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    public static (int Width, int Height) Fit(int width, int height, int longEdge)
    {
        var longest = Math.Max(width, height);
        if (longest <= longEdge) return (width, height);
        var scale = (double)longEdge / longest;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static bool SwapsAxes(SKEncodedOrigin origin) =>
        origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    // Decodes at the smallest size the codec offers that still covers the target: a JPEG decodes at
    // 1/2, 1/4 or 1/8 directly, so a 50-megapixel upload never becomes a 200 MB bitmap in memory.
    private static SKBitmap? Decode(SKCodec codec, int neededWidth, int neededHeight)
    {
        var info = codec.Info;
        var size = new SKSizeI(info.Width, info.Height);
        var desired = Math.Min(1f, Math.Max((float)neededWidth / info.Width, (float)neededHeight / info.Height));
        foreach (var candidate in new[] { desired, desired * 1.25f, desired * 1.5f, desired * 2f })
        {
            if (candidate >= 1f) break;
            var scaled = codec.GetScaledDimensions(candidate);
            if (scaled.Width >= neededWidth && scaled.Height >= neededHeight) { size = scaled; break; }
        }

        var alpha = info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul;
        var target = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, alpha);
        var bitmap = new SKBitmap(target);
        var result = codec.GetPixels(target, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
        {
            bitmap.Dispose();
            return null;
        }
        return bitmap;
    }

    private static bool HasTransparency(SKBitmap bitmap)
    {
        var pixels = bitmap.GetPixelSpan();
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255) return true;
        }
        return false;
    }

    // Draws the decoded bitmap upright (the EXIF orientation applied) at width x height, then encodes.
    private static Rendition Render(SKBitmap source, SKEncodedOrigin origin, int width, int height, bool transparent)
    {
        var swap = SwapsAxes(origin);
        var drawWidth = swap ? height : width;
        var drawHeight = swap ? width : height;

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, transparent ? SKAlphaType.Premul : SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(transparent ? SKColors.Transparent : SKColors.White);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(width, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(width, height); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, height); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(width, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(width, height); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, height); canvas.RotateDegrees(-90); break;
        }

        using var image = SKImage.FromBitmap(source);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(image, new SKRect(0, 0, drawWidth, drawHeight), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);

        using var snapshot = surface.Snapshot();
        using var encoded = transparent
            ? snapshot.Encode(SKEncodedImageFormat.Png, 100)
            : snapshot.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return transparent
            ? new Rendition(encoded.ToArray(), "image/png", ".png", width, height)
            : new Rendition(encoded.ToArray(), "image/jpeg", ".jpg", width, height);
    }
}
