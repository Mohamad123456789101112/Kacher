using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kacher.Pos.Services.Printing;

/// <summary>
/// Converts the store logo to an ESC/POS raster bitmap: 1 bit per pixel, rows padded to a byte,
/// MSB first, black = 1 (the convention <c>GS v 0</c> expects).
///
/// WPF imaging APIs are used instead of System.Drawing so no extra native dependency ships with
/// the terminal. Frozen <see cref="BitmapSource"/> instances are safe to use from any thread, which
/// matters because printing happens off the dispatcher.
/// </summary>
public static class LogoRasterizer
{
    public sealed record Raster(byte[] Data, int WidthBytes, int HeightDots);

    private static readonly Dictionary<(string Path, int Width), Raster?> Cache = new();
    private static readonly object Gate = new();

    public static Raster? TryRasterize(string? path, int targetWidthDots = 384)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue((path, targetWidthDots), out var cached))
            {
                return cached;
            }

            try
            {
                var raster = Rasterize(path, targetWidthDots);
                Cache[(path, targetWidthDots)] = raster;
                return raster;
            }
            catch (Exception)
            {
                // A broken logo must never block a sale.
                Cache[(path, targetWidthDots)] = null;
                return null;
            }
        }
    }

    private static Raster Rasterize(string path, int targetWidthDots)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.None;
        image.EndInit();
        image.Freeze();

        // Monochrome + scale to the target width, preserving aspect ratio.
        var scale = Math.Min(1.0, (double)targetWidthDots / image.PixelWidth);
        var scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        scaled.Freeze();

        var gray = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);
        gray.Freeze();

        var width = gray.PixelWidth;
        var height = gray.PixelHeight;
        var widthBytes = (width + 7) / 8;

        var pixels = new byte[width * height];
        gray.CopyPixels(pixels, width, 0);

        var output = new byte[widthBytes * height];
        const byte threshold = 160; // mid-grey: keeps thin strokes from disappearing

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * width;
            var outOffset = y * widthBytes;

            for (var x = 0; x < width; x++)
            {
                if (pixels[rowOffset + x] < threshold)
                {
                    output[outOffset + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }

        return new Raster(output, widthBytes, height);
    }
}
