using System.Windows;
using System.Windows.Media;
using Kacher.Pos.Services;
using MaterialDesignColors;
using MaterialDesignThemes.Wpf;

namespace Kacher.Pos.Infrastructure;

public interface IThemeService
{
    bool IsDark { get; }

    Task InitializeAsync(CancellationToken ct = default);

    Task ApplyAsync(bool dark, bool persist = true);
}

/// <summary>
/// Runtime Dark/Light switching.
///
/// Two independent layers are updated:
///  1. <see cref="BundledTheme"/>.BaseTheme — Material Design's own palette (control backgrounds,
///     text roles, elevation). Setting the property makes the dictionary re-generate, which is the
///     supported v5 approach and avoids rebuilding the visual tree.
///  2. The app's semantic brushes (Kacher.Surface, Kacher.TextPrimary …) so custom templates in
///     Themes/Styles.xaml follow instantly without a restart.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private readonly IAppSettingsService _settings;
    private readonly IAppLog _log;

    public ThemeService(IAppSettingsService settings, IAppLog log)
    {
        _settings = settings;
        _log = log;
    }

    public bool IsDark { get; private set; } = true;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var stored = await _settings.GetAsync("ui.theme", ct).ConfigureAwait(false);
        var dark = !string.Equals(stored, "Light", StringComparison.OrdinalIgnoreCase);
        await ApplyAsync(dark, persist: false).ConfigureAwait(false);
    }

    public Task ApplyAsync(bool dark, bool persist = true)
    {
        IsDark = dark;

        // Resource dictionaries must be touched on the dispatcher thread.
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null)
        {
            return Task.CompletedTask;
        }

        dispatcher.Invoke(() =>
        {
            ApplyMaterialBaseTheme(dark);
            ApplySemanticBrushes(dark);
        });

        if (persist)
        {
            _ = _settings.SetAsync("ui.theme", dark ? "Dark" : "Light");
        }

        _log.Info($"Theme switched to {(dark ? "dark" : "light")}.");
        return Task.CompletedTask;
    }

    private static void ApplyMaterialBaseTheme(bool dark)
    {
        var resources = Application.Current.Resources;
        var bundled = resources.MergedDictionaries.OfType<BundledTheme>().FirstOrDefault();

        if (bundled is not null)
        {
            bundled.BaseTheme = dark ? BaseTheme.Dark : BaseTheme.Light;
            return;
        }

        // Fallback for a custom composition: rebuild the theme dictionary in place.
        resources.MergedDictionaries.Insert(0, new BundledTheme
        {
            BaseTheme = dark ? BaseTheme.Dark : BaseTheme.Light,
            PrimaryColor = PrimaryColor.Teal,
            SecondaryColor = SecondaryColor.Amber
        });
    }

    private static void ApplySemanticBrushes(bool dark)
    {
        var palette = dark
            ? new Dictionary<string, string>
            {
                ["Kacher.Surface"] = "#FF101A1C",
                ["Kacher.SurfaceAlt"] = "#FF162326",
                ["Kacher.SurfaceRaised"] = "#FF1D2D31",
                ["Kacher.SurfaceHigh"] = "#FF223438",
                ["Kacher.Divider"] = "#FF2A3C40",
                ["Kacher.TextPrimary"] = "#FFF1F5F6",
                ["Kacher.TextSecondary"] = "#FFB4C4C7",
                ["Kacher.TextMuted"] = "#FF7E9295",
                ["Kacher.AccentSoft"] = "#3314B8A6"
            }
            : new Dictionary<string, string>
            {
                ["Kacher.Surface"] = "#FFF6F8F8",
                ["Kacher.SurfaceAlt"] = "#FFFFFFFF",
                ["Kacher.SurfaceRaised"] = "#FFEDF2F2",
                ["Kacher.SurfaceHigh"] = "#FFE4ECEC",
                ["Kacher.Divider"] = "#FFD5DEDE",
                ["Kacher.TextPrimary"] = "#FF0B1416",
                ["Kacher.TextSecondary"] = "#FF42585C",
                ["Kacher.TextMuted"] = "#FF687E82",
                ["Kacher.AccentSoft"] = "#2614B8A6"
            };

        foreach (var (key, hex) in palette)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
    }
}
