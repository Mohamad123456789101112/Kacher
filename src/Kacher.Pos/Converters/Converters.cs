using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kacher.Pos.Models;

namespace Kacher.Pos.Converters;

/// <summary>Inverts a boolean — used to disable a control while a command runs.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && !b;
}

/// <summary>Collapsed when the value is true (i.e. Visible == !value).</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

/// <summary>
/// Visible when the value is not null/empty. Pass "invert" as the parameter to flip the logic.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true
        };

        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        return (hasValue ^ invert) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Formats money with the store currency, e.g. "125.50 EGP".</summary>
public sealed class CurrencyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var amount = value switch
        {
            decimal d => d,
            double dbl => (decimal)dbl,
            int i => i,
            _ => 0m
        };

        var format = parameter as string ?? "0.00";
        return amount.ToString(format, CultureInfo.InvariantCulture) + " EGP";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Maps <see cref="HardwareHealth"/> to the status pill colour.</summary>
public sealed class HardwareHealthToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            HardwareHealth.Online => "Kacher.HardwareOnline",
            HardwareHealth.Degraded => "Kacher.HardwareDegraded",
            HardwareHealth.Offline => "Kacher.HardwareOffline",
            _ => "Kacher.HardwareUnknown"
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Loads a product image from the local image store. Returns null (silently) when the file is
/// missing so the tile falls back to its colour block — no broken-image placeholder at the till.
/// </summary>
public sealed class ImagePathToSourceConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapImage?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            try
            {
                var full = Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(Data.DatabaseConnectionFactory.ImagesDirectory, path);

                if (!File.Exists(full))
                {
                    Cache[path] = null;
                    return null;
                }

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                // Decode at display size: a 4K product photo otherwise costs 24 MB of VRAM per tile.
                image.DecodePixelWidth = 320;
                image.UriSource = new Uri(full, UriKind.Absolute);
                image.EndInit();
                image.Freeze();

                Cache[path] = image;
                return image;
            }
            catch (Exception)
            {
                Cache[path] = null;
                return null;
            }
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Converts "#RRGGBB" into a frozen brush for category/product accents.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value as string;

        if (string.IsNullOrWhiteSpace(hex))
        {
            hex = "#334155";
        }

        lock (Cache)
        {
            if (Cache.TryGetValue(hex, out var cached))
            {
                return cached;
            }

            try
            {
                var brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
                if (brush.CanFreeze)
                {
                    brush.Freeze();
                }
                Cache[hex] = brush;
                return brush;
            }
            catch (FormatException)
            {
                var fallback = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));
                fallback.Freeze();
                Cache[hex] = fallback;
                return fallback;
            }
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Collapses an element when a collection count is zero.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value switch
        {
            int i => i,
            System.Collections.ICollection c => c.Count,
            _ => 0
        };

        var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        return (count > 0) ^ invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>True when the bound value equals the converter parameter (radio-button groups).</summary>
public sealed class EqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? parameter : Binding.DoNothing;
}

/// <summary>
/// Scales a 0..1 ratio into a pixel height, used by the hourly revenue bars.
/// The bar height is the converter parameter (default 120 px).
/// </summary>
public sealed class RatioToHeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ratio = value switch
        {
            double d => d,
            decimal m => (double)m,
            float f => f,
            _ => 0d
        };

        var max = 120d;
        if (parameter is string text && double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            max = parsed;
        }

        // Minimum 3 px so hours with a tiny amount of trade are still visible.
        return Math.Max(3d, Math.Clamp(ratio, 0, 1) * max);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Percentage (0..1) → GridLength star for simple bar charts in the reports view.</summary>
public sealed class RatioToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ratio = value is decimal d ? (double)d : value is double dbl ? dbl : 0;
        return new GridLength(Math.Clamp(ratio, 0, 1) * 100, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// Resolves an icon resource key (e.g. <c>"Icon.Cart"</c>) to the <see cref="Geometry"/> declared in
/// <c>Themes/Icons.xaml</c>. Keeps icon choice in the view model as a plain string — no WPF types
/// leak into the presentation logic — while the artwork stays declarative in XAML.
/// </summary>
public sealed class ResourceIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        // TryFindResource also walks merged dictionaries, so the app-level icon set is found from
        // any view, dialog or control template.
        return Application.Current?.TryFindResource(key) as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Hex string → <see cref="System.Windows.Media.Color"/>. Needed wherever a colour has to be *part*
/// of a brush (a gradient stop, a drop shadow) rather than the brush itself.
/// </summary>
public sealed class HexToColorConverter : IValueConverter
{
    private static readonly Dictionary<string, Color> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value as string;

        if (string.IsNullOrWhiteSpace(hex))
        {
            return Color.FromRgb(0x33, 0x41, 0x55);
        }

        lock (Cache)
        {
            if (Cache.TryGetValue(hex, out var cached))
            {
                return cached;
            }

            try
            {
                var converted = (Color)ColorConverter.ConvertFromString(hex)!;
                Cache[hex] = converted;
                return converted;
            }
            catch (FormatException)
            {
                return Color.FromRgb(0x33, 0x41, 0x55);
            }
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
