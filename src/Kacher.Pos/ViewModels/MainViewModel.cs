using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Kacher.Pos.Services;
using Kacher.Pos.Services.Hardware;
using Kacher.Pos.ViewModels.Items;
using Microsoft.Extensions.DependencyInjection;

namespace Kacher.Pos.ViewModels;

/// <summary>
/// Shell view model: navigation, session chrome (cashier, shift, clock), hardware pills and the
/// title-bar actions.
///
/// Views are resolved from DI on demand and cached per key, so switching tabs does not recreate
/// view models (and therefore does not re-query SQLite) on every click.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ISessionService _session;
    private readonly IHardwareMonitor _monitor;
    private readonly IThemeService _theme;
    private readonly IShellService _shell;
    private readonly IReceiptPrinter _printer;
    private readonly Services.Repositories.IHardwareConfigRepository _configs;

    private readonly Dictionary<string, ViewModelBase> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _clockTimer;

    public MainViewModel(
        IServiceProvider services,
        ISessionService session,
        IHardwareMonitor monitor,
        IThemeService theme,
        IShellService shell,
        IReceiptPrinter printer,
        Services.Repositories.IHardwareConfigRepository configs,
        IUserNotifier notifier) : base(notifier)
    {
        _services = services;
        _session = session;
        _monitor = monitor;
        _theme = theme;
        _shell = shell;
        _printer = printer;
        _configs = configs;

        Title = "Kacher POS";

        NavItems = new ObservableCollection<NavItemViewModel>
        {
            new("Dashboard", "Icon.Grid", "dashboard", Permissions.ViewReports),
            new("Sales", "Icon.Sales", "sales", Permissions.ProcessSale),
            new("Invoices", "Icon.Receipt", "invoices", Permissions.ViewReports),
            new("Products", "Icon.Products", "products", Permissions.ViewCatalogue),
            new("Inventory", "Icon.Inventory", "inventory", Permissions.ViewCatalogue),
            new("Reports", "Icon.Reports", "reports", Permissions.ViewReports),
            new("Shift", "Icon.Shift", "shift", Permissions.ProcessSale),
            new("Settings", "Icon.Settings", "settings", null)
        };

        _session.SessionChanged += OnSessionChanged;
        _monitor.StatusChanged += OnHardwareStatusChanged;
        Hardware = _monitor.Current;

        // 1 Hz clock: a DispatcherTimer is correct here because it is UI-bound by definition and
        // costs a few microseconds per tick.
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            ClockText = now.ToString("HH:mm:ss");

            // Only raise the date when the day actually changes — avoids a pointless rebind every tick.
            var today = now.ToString("dddd, d MMMM");
            if (!string.Equals(DateText, today, StringComparison.Ordinal))
            {
                DateText = today;
            }
        };
        _clockTimer.Start();

        ClockText = DateTime.Now.ToString("HH:mm:ss");
        RefreshSessionChrome();
        Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    }

    // ── Navigation ───────────────────────────────────────────────────────────────────────────

    public ObservableCollection<NavItemViewModel> NavItems { get; }

    [ObservableProperty]
    private NavItemViewModel? _selectedNavItem;

    [ObservableProperty]
    private ViewModelBase? _currentViewModel;

    [ObservableProperty]
    private bool _isNavigationExpanded = true;

    [ObservableProperty]
    private string _headerTitle = "Sales";

    [ObservableProperty]
    private string _headerSubtitle = string.Empty;

    // ── Session chrome ───────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _cashierName = "—";

    [ObservableProperty]
    private string _roleText = "—";

    [ObservableProperty]
    private string _shiftText = "No open shift";

    [ObservableProperty]
    private bool _shiftIsOpen;

    [ObservableProperty]
    private string _clockText = string.Empty;

    [ObservableProperty]
    private string _dateText = DateTime.Now.ToString("dddd, d MMMM");

    /// <summary>Single letter shown in the cashier avatar bubble.</summary>
    [ObservableProperty]
    private string _cashierInitial = "?";

    [ObservableProperty]
    private string _version = "1.0.0";

    [ObservableProperty]
    private bool _isDarkTheme = true;

    // ── Hardware pills ───────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private HardwareSnapshot _hardware = HardwareSnapshot.Unknown;

    public HardwareHealth PrinterHealth => Hardware.Printer;
    public HardwareHealth ScannerHealth => Hardware.Scanner;
    public HardwareHealth DrawerHealth => Hardware.Drawer;
    public string PrinterDetail => Hardware.PrinterDetail;
    public string ScannerDetail => Hardware.ScannerDetail;
    public string DrawerDetail => Hardware.DrawerDetail;

    /// <summary>Icon resource key for the theme toggle (resolved by the IconKey converter).</summary>
    public string ThemeIcon => IsDarkTheme ? "Icon.Moon" : "Icon.Sun";

    // ── Commands ─────────────────────────────────────────────────────────────────────────────

    private bool _navigating;

    /// <summary>Selection is the navigation trigger — clicking the rail or calling NavigateCommand.</summary>
    partial void OnSelectedNavItemChanged(NavItemViewModel? value)
    {
        if (value is null || _navigating)
        {
            return;
        }

        _ = NavigateAsync(value);
    }

    [RelayCommand]
    private async Task NavigateAsync(NavItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.Permission is { } permission && !_session.HasPermission(permission))
        {
            Notifier.Warn($"Your role does not have access to {item.Label}.");

            _navigating = true;
            SelectedNavItem = NavItems.FirstOrDefault(n => n.ViewKey == CurrentKey);
            _navigating = false;
            return;
        }

        _navigating = true;
        try
        {
            await ShowAsync(item.ViewKey).ConfigureAwait(true);
            SelectedNavItem = item;
        }
        finally
        {
            _navigating = false;
        }
    }

    private string CurrentKey { get; set; } = "sales";

    /// <summary>Navigates programmatically (used after sign-in and by deep links).</summary>
    public async Task ShowAsync(string key)
    {
        var viewModel = GetOrCreate(key);

        if (CurrentViewModel is { } previous && !ReferenceEquals(previous, viewModel))
        {
            await previous.UnloadAsync().ConfigureAwait(true);
        }

        // Avoid re-running LoadAsync on every tab switch; views expose explicit refresh commands.
        if (!_loaded.Contains(key))
        {
            await viewModel.LoadAsync().ConfigureAwait(true);
            _loaded.Add(key);
        }

        CurrentViewModel = viewModel;
        CurrentKey = key;
        HeaderTitle = viewModel.Title;
        HeaderSubtitle = Describe(key);
    }

    private readonly HashSet<string> _loaded = new(StringComparer.OrdinalIgnoreCase);

    private ViewModelBase GetOrCreate(string key)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ViewModelBase viewModel = key switch
        {
            "dashboard" => CreateDashboard(),
            "sales" => _services.GetRequiredService<SalesViewModel>(),
            "invoices" => _services.GetRequiredService<InvoicesViewModel>(),
            "products" => _services.GetRequiredService<ProductsViewModel>(),
            "inventory" => _services.GetRequiredService<InventoryViewModel>(),
            "reports" => _services.GetRequiredService<ReportsViewModel>(),
            "shift" => _services.GetRequiredService<ShiftViewModel>(),
            "settings" => _services.GetRequiredService<SettingsViewModel>(),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown view key.")
        };

        // View models stay UI-agnostic: modal prompts arrive as an optional capability, not a ctor arg.
        viewModel.AttachDialogs(_services.GetRequiredService<IDialogService>());

        _cache[key] = viewModel;
        return viewModel;
    }

    /// <summary>
    /// The dashboard is created here (not registered directly) because it needs a navigation hook back
    /// into the shell; wiring it as an event keeps the dependency one-way.
    /// </summary>
    private ViewModelBase CreateDashboard()
    {
        var dashboard = _services.GetRequiredService<DashboardViewModel>();
        dashboard.NavigateRequested += async (_, key) =>
        {
            try
            {
                await ShowAsync(key).ConfigureAwait(true);

                _navigating = true;
                SelectedNavItem = NavItems.FirstOrDefault(n => n.ViewKey == key);
                _navigating = false;
            }
            catch (Exception ex)
            {
                Notifier.Error($"Could not navigate to {key}: {ex.Message}");
            }
        };

        return dashboard;
    }

    private static string Describe(string key) => key switch
    {
        "dashboard" => "Today at a glance — sales, stock alerts and best sellers.",
        "sales" => "Scan, tap, take payment. Shift+F1 opens the drawer.",
        "invoices" => "Search past sales, reprint a receipt or issue a refund.",
        "products" => "Catalogue maintenance and pricing",
        "inventory" => "Stock levels, adjustments and movement ledger",
        "reports" => "Sales performance and exports",
        "shift" => "Drawer sessions and end-of-day",
        "settings" => "Appearance, operators, hardware",
        _ => string.Empty
    };

    [RelayCommand]
    private void ToggleNavigation() => IsNavigationExpanded = !IsNavigationExpanded;

    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        await _theme.ApplyAsync(!IsDarkTheme).ConfigureAwait(true);
        IsDarkTheme = _theme.IsDark;
        OnPropertyChanged(nameof(ThemeIcon));
    }

    [RelayCommand]
    private async Task OpenDrawerAsync()
    {
        await RunAsync(async ct =>
        {
            var config = await _configs.GetActiveAsync(ct).ConfigureAwait(true);
            var result = await _printer.OpenDrawerAsync(config, ct).ConfigureAwait(true);

            if (result.Success)
            {
                Notifier.Success("Cash drawer opened.");
            }
            else
            {
                Notifier.Error($"Drawer did not open: {result.Message}");
            }
        }, "Could not open the drawer.");
    }

    [RelayCommand]
    private void RefreshHardware() => _ = _monitor.RefreshAsync();

    [RelayCommand]
    private void Minimize() => _shell.Minimize();

    [RelayCommand]
    private void ToggleMaximize() => _shell.ToggleMaximize();

    [RelayCommand]
    private void Close() => _shell.Close();

    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (!await _services.GetRequiredService<IDialogService>()
                .ConfirmAsync("Sign out", "End this session and return to the PIN screen?", "Sign out")
                .ConfigureAwait(true))
        {
            return;
        }

        _session.SignOut();
        _shell.SignOut();
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────────────────────

    public override async Task LoadAsync(CancellationToken ct = default)
    {
        await _monitor.StartAsync(ct).ConfigureAwait(true);
        await _theme.InitializeAsync(ct).ConfigureAwait(true);

        IsDarkTheme = _theme.IsDark;
        OnPropertyChanged(nameof(ThemeIcon));

        RefreshSessionChrome();

        // No open drawer session → take the operator straight to the shift screen so the first sale
        // cannot fail with "no shift open".
        if (!_session.ShiftIsOpen && _session.HasPermission(Permissions.ProcessSale))
        {
            await ShowAsync("shift").ConfigureAwait(true);
            SelectedNavItem = NavItems.FirstOrDefault(n => n.ViewKey == "shift");
            return;
        }

        // Landing screen: managers get the dashboard, cashiers go straight to the till.
        var landing = _session.HasPermission(Permissions.ViewReports) ? "dashboard" : "sales";

        await ShowAsync(landing).ConfigureAwait(true);
        SelectedNavItem = NavItems.FirstOrDefault(n => n.ViewKey == landing);
    }

    private void RefreshSessionChrome()
    {
        CashierName = _session.CashierName;
        CashierInitial = string.IsNullOrEmpty(CashierName) || CashierName == "—"
            ? "?"
            : CashierName[..1].ToUpperInvariant();
        RoleText = _session.Role.ToString();
        ShiftIsOpen = _session.ShiftIsOpen;
        ShiftText = _session.CurrentShift is { } shift
            ? $"{shift.ShiftNumber} · open"
            : "No open shift";
    }

    private void OnSessionChanged(object? sender, EventArgs e) => RefreshSessionChrome();

    private void OnHardwareStatusChanged(object? sender, HardwareSnapshot snapshot)
    {
        // The monitor raises from a background task; hop to the UI thread for binding safety.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply(snapshot);
            return;
        }

        dispatcher.BeginInvoke(new Action(() => Apply(snapshot)));
    }

    private void Apply(HardwareSnapshot snapshot)
    {
        Hardware = snapshot;
        OnPropertyChanged(nameof(PrinterHealth));
        OnPropertyChanged(nameof(ScannerHealth));
        OnPropertyChanged(nameof(DrawerHealth));
        OnPropertyChanged(nameof(PrinterDetail));
        OnPropertyChanged(nameof(ScannerDetail));
        OnPropertyChanged(nameof(DrawerDetail));
    }

    public void Dispose()
    {
        _clockTimer.Stop();
        _session.SessionChanged -= OnSessionChanged;
        _monitor.StatusChanged -= OnHardwareStatusChanged;

        foreach (var viewModel in _cache.Values)
        {
            if (viewModel is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
