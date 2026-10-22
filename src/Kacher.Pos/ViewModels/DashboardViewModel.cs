using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Kacher.Pos.Services;
using Kacher.Pos.Services.Repositories;
using Kacher.Pos.ViewModels.Items;

namespace Kacher.Pos.ViewModels;

/// <summary>
/// The screen a manager opens first: today at a glance, what needs attention, and the best sellers.
/// </summary>
/// <remarks>
/// Deliberately read-only and cheap: one daily report query, one low-stock query and one shift
/// lookup, all awaited in sequence so a slow disk cannot stack three context creations at once.
/// Navigation is delegated to <see cref="IShellService"/> so the view model never touches a Window.
/// </remarks>
public sealed partial class DashboardViewModel : ViewModelBase
{
    private readonly IReportService _reports;
    private readonly IProductRepository _products;
    private readonly IShiftService _shifts;
    private readonly ISessionService _session;

    public DashboardViewModel(
        IReportService reports,
        IProductRepository products,
        IShiftService shifts,
        ISessionService session,
        IUserNotifier notifier) : base(notifier)
    {
        _reports = reports;
        _products = products;
        _shifts = shifts;
        _session = session;

        Title = "Dashboard";
    }

    public ObservableCollection<KpiCardViewModel> Kpis { get; } = new();

    public ObservableCollection<AttentionItemViewModel> Attention { get; } = new();

    public ObservableCollection<TopSellerViewModel> TopSellers { get; } = new();

    public ObservableCollection<HourlyBarViewModel> Hourly { get; } = new();

    [ObservableProperty]
    private string _greeting = "Welcome back";

    [ObservableProperty]
    private string _periodLabel = string.Empty;

    [ObservableProperty]
    private string _shiftLabel = "No shift open";

    [ObservableProperty]
    private bool _hasAttention;

    [ObservableProperty]
    private bool _hasTopSellers;

    [ObservableProperty]
    private string _peakHourText = "—";

    public override Task LoadAsync(CancellationToken ct = default) => RefreshAsync();

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(RefreshCoreAsync, "Could not load the dashboard.");

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var report = await _reports.GetDailyReportAsync(today, ct).ConfigureAwait(true);

        Greeting = GreetingFor(DateTime.Now);
        PeriodLabel = DateTime.Today.ToString("dddd, d MMMM yyyy");
        ShiftLabel = _session.CurrentShift is { } shift
            ? $"Shift {shift.ShiftNumber} · open since {shift.OpenedAtUtc.ToLocalTime():HH:mm}"
            : "No shift open — open one before the first sale";

        // ── KPIs ─────────────────────────────────────────────────────────────────────────────
        Kpis.Clear();
        Kpis.Add(new KpiCardViewModel("Today's sales", $"{report.GrossSales:0.00} EGP",
            $"{report.InvoiceCount} invoices", "Icon.Cash", "#14B8A6"));
        Kpis.Add(new KpiCardViewModel("Average basket", $"{report.AverageBasket:0.00} EGP",
            "per invoice", "Icon.Trend", "#38BDF8"));
        Kpis.Add(new KpiCardViewModel("Tax collected", $"{report.TaxTotal:0.00} EGP",
            "VAT across all rates", "Icon.Receipt", "#A78BFA"));
        Kpis.Add(new KpiCardViewModel("Cash vs card", $"{report.CashTotal:0.00} / {report.CardTotal:0.00}",
            "EGP", "Icon.Card", "#FBBF24"));

        // ── Hourly strip ─────────────────────────────────────────────────────────────────────
        Hourly.Clear();
        var maxRevenue = report.Hourly.Count == 0 ? 0m : report.Hourly.Max(h => h.Revenue);

        foreach (var hour in report.Hourly)
        {
            Hourly.Add(new HourlyBarViewModel(hour, maxRevenue));
        }

        PeakHourText = report.Hourly.Count == 0
            ? "—"
            : $"{report.Hourly.MaxBy(h => h.Revenue)!.Hour:00}:00";

        // ── Top sellers ──────────────────────────────────────────────────────────────────────
        TopSellers.Clear();

        var top = report.TopProducts.Take(6).ToList();
        var maxProduct = top.Count == 0 ? 0m : top.Max(p => p.Revenue);
        var rank = 1;

        foreach (var product in top)
        {
            TopSellers.Add(new TopSellerViewModel(product, rank++, maxProduct));
        }

        HasTopSellers = TopSellers.Count > 0;

        // ── Attention list ───────────────────────────────────────────────────────────────────
        Attention.Clear();

        var lowStock = await _products
            .GetCatalogueAsync(categoryId: null, search: null, lowStockOnly: true, ct)
            .ConfigureAwait(true);

        if (lowStock.Count > 0)
        {
            Attention.Add(new AttentionItemViewModel(
                $"{lowStock.Count} product{(lowStock.Count == 1 ? string.Empty : "s")} at or below reorder level",
                string.Join(", ", lowStock.Take(4).Select(p => p.Name)) +
                    (lowStock.Count > 4 ? $" and {lowStock.Count - 4} more" : string.Empty),
                "Icon.Warning",
                "#F59E0B",
                "inventory",
                "Review stock"));
        }

        if (_session.CurrentShift is null)
        {
            Attention.Add(new AttentionItemViewModel(
                "No shift is open",
                "Sales are blocked until a drawer session is opened.",
                "Icon.Shift",
                "#EF4444",
                "shift",
                "Open shift"));
        }

        if (_session.CurrentShift is { } openShift)
        {
            var summary = await _shifts.BuildSummaryAsync(openShift.Id, ct: ct).ConfigureAwait(true);
            var cash = summary.ExpectedCash;

            Attention.Add(new AttentionItemViewModel(
                "Drawer expected cash",
                $"{cash:0.00} EGP should be in the drawer right now " +
                $"(float {summary.OpeningFloat:0.00} + cash {summary.CashTotal:0.00}).",
                "Icon.Drawer",
                "#14B8A6",
                "shift",
                "Reconcile"));
        }

        if (report.RefundTotal > 0)
        {
            Attention.Add(new AttentionItemViewModel(
                $"Refunds today: {report.RefundTotal:0.00} EGP",
                "Review the refunded invoices before closing the shift.",
                "Icon.Undo",
                "#F97316",
                "invoices",
                "Open invoices"));
        }

        HasAttention = Attention.Count > 0;
    }

    private static string GreetingFor(DateTime now) => now.Hour switch
    {
        < 6 => "Good evening",
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        < 22 => "Good evening",
        _ => "Good night"
    };

    [RelayCommand]
    private void Open(string? viewKey)
    {
        if (string.IsNullOrWhiteSpace(viewKey))
        {
            return;
        }

        NavigateRequested?.Invoke(this, viewKey);
    }

    /// <summary>
    /// Raised when a tile or an attention row asks for another screen. The shell subscribes and
    /// performs the navigation — this keeps the view model free of navigation state.
    /// </summary>
    public event EventHandler<string>? NavigateRequested;
}
