using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kacher.Pos.Services.Printing;

/// <summary>1-bit, row-major bitmap ready for <c>GS v 0</c>.</summary>
public sealed record RasterImage(int WidthDots, int HeightDots, byte[] Data);

/// <summary>
/// Converts the store logo into the monochrome raster an ESC/POS printer can consume.
/// </summary>
/// <remarks>
/// Thermal printers have no grey levels: every dot is on or off. The image is therefore scaled so
/// one pixel maps to one dot (384 dots on an 80 mm roll, 288 on 58 mm), converted to 8-bit grey and
/// thresholded at 50 %. That is enough for a logo; photographs need error-diffusion dithering, which
/// is out of scope for a receipt header.
/// </remarks>
public static class LogoRasterizer
{
    private static readonly Dictionary<(string Path, int Width), RasterImage?> Cache = new();

    /// <summary>Returns <c>null</c> when the file is missing or cannot be decoded — never throws.</summary>
    public static RasterImage? FromFile(string? path, int maxWidthDots)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || maxWidthDots <= 0)
        {
            return null;
        }

        lock (Cache)
        {
            if (Cache.TryGetValue((path, maxWidthDots), out var cached))
            {
                return cached;
            }

            var raster = Render(path, maxWidthDots);
            Cache[(path, maxWidthDots)] = raster;
            return raster;
        }
    }

    private static RasterImage? Render(string path, int maxWidthDots)
    {
        try
        {
            // OnLoad + a frozen frame: the file handle is released immediately and the bitmap can be
            // touched from any thread, which matters because printing runs off the dispatcher.
            var decoder = BitmapDecoder.Create(
                new Uri(Path.GetFullPath(path)),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);

            var source = decoder.Frames[0];

            // Only ever shrink: a small logo stays pixel-exact instead of being blown up.
            var scale = Math.Min(1.0, (double)maxWidthDots / source.PixelWidth);
            BitmapSource scaled = scale < 1.0
                ? new TransformedBitmap(source, new ScaleTransform(scale, scale))
                : source;

            var gray = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);

            // Freezing makes the bitmap immutable and safe to read from the printing thread.
            try
            {
                if (gray.CanFreeze)
                {
                    gray.Freeze();
                }
            }
            catch (InvalidOperationException)
            {
                // Non-freezable decoder output is still readable on the calling thread.
            }

            var width = gray.PixelWidth;
            var height = gray.PixelHeight;
            var stride = width;
            var pixels = new byte[stride * height];
            gray.CopyPixels(pixels, stride, 0);

            var bytesPerRow = (width + 7) / 8;
            var data = new byte[bytesPerRow * height];

            for (var y = 0; y < height; y++)
            {
                var rowStart = y * stride;

                for (var x = 0; x < width; x++)
                {
                    // Gray8: 0 is black, 255 is white. Black pixels become set bits, MSB first.
                    if (pixels[rowStart + x] < 128)
                    {
                        data[(y * bytesPerRow) + (x / 8)] |= (byte)(0x80 >> (x % 8));
                    }
                }
            }

            return new RasterImage(width, height, data);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or FileFormatException)
        {
            return null;
        }
    }
}
