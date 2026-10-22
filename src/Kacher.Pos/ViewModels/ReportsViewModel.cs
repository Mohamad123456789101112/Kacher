using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Kacher.Pos.Services;
using Kacher.Pos.Services.Pdf;
using Kacher.Pos.Services.Repositories;
using Kacher.Pos.ViewModels.Items;

namespace Kacher.Pos.ViewModels;

/// <summary>Chart bar for the hourly revenue strip.</summary>
public sealed class HourlyBarViewModel
{
    public HourlyBarViewModel(HourlySales sales, decimal maxRevenue)
    {
        Label = $"{sales.Hour:00}";
        Revenue = sales.Revenue;
        Invoices = sales.InvoiceCount;
        Ratio = maxRevenue <= 0 ? 0 : (double)(sales.Revenue / maxRevenue);
        Tooltip = $"{sales.Hour:00}:00 — {sales.InvoiceCount} invoices, {sales.Revenue:0.00} EGP";
    }

    public string Label { get; }
    public decimal Revenue { get; }
    public int Invoices { get; }
    public double Ratio { get; }
    public string Tooltip { get; }
}

/// <summary>Sales reporting: daily KPIs, hourly distribution, top sellers, invoice history, exports.</summary>
public sealed partial class ReportsViewModel : ViewModelBase
{
    private readonly IReportService _reports;
    private readonly IHardwareConfigRepository _hardware;
    private readonly IPdfReceiptService _pdf;

    public ReportsViewModel(
        IReportService reports,
        IHardwareConfigRepository hardware,
        IPdfReceiptService pdf,
        IUserNotifier notifier) : base(notifier)
    {
        _reports = reports;
        _hardware = hardware;
        _pdf = pdf;

        Title = "Reports";
        SelectedDate = DateTime.Today;
    }

    public ObservableCollection<HourlyBarViewModel> Hourly { get; } = new();

    public ObservableCollection<TopProduct> TopProducts { get; } = new();

    public ObservableCollection<InvoiceRowViewModel> Invoices { get; } = new();

    public ObservableCollection<TaxBand> TaxBands { get; } = new();

    [ObservableProperty]
    private DateTime _selectedDate = DateTime.Today;

    [ObservableProperty]
    private int _invoiceCount;

    [ObservableProperty]
    private decimal _grossSales;

    [ObservableProperty]
    private decimal _netSales;

    [ObservableProperty]
    private decimal _taxTotal;

    [ObservableProperty]
    private decimal _discountTotal;

    [ObservableProperty]
    private decimal _cashTotal;

    [ObservableProperty]
    private decimal _cardTotal;

    [ObservableProperty]
    private decimal _refundTotal;

    [ObservableProperty]
    private decimal _averageBasket;

    [ObservableProperty]
    private InvoiceRowViewModel? _selectedInvoice;

    [ObservableProperty]
    private bool _isBusyExport;

    public override Task LoadAsync(CancellationToken ct = default) => RefreshAsync();

    partial void OnSelectedDateChanged(DateTime value) => _ = RefreshAsync();

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async ct =>
    {
        var date = DateOnly.FromDateTime(SelectedDate);
        var report = await _reports.GetDailyReportAsync(date, ct).ConfigureAwait(true);

        InvoiceCount = report.InvoiceCount;
        GrossSales = report.GrossSales;
        NetSales = report.NetSales;
        TaxTotal = report.TaxTotal;
        DiscountTotal = report.DiscountTotal;
        CashTotal = report.CashTotal;
        CardTotal = report.CardTotal;
        RefundTotal = report.RefundTotal;
        AverageBasket = report.AverageBasket;

        var max = report.Hourly.Count == 0 ? 0m : report.Hourly.Max(h => h.Revenue);

        Hourly.Clear();
        foreach (var hour in report.Hourly)
        {
            Hourly.Add(new HourlyBarViewModel(hour, max));
        }

        TopProducts.Clear();
        foreach (var product in report.TopProducts)
        {
            TopProducts.Add(product);
        }

        TaxBands.Clear();
        foreach (var band in report.TaxBands)
        {
            TaxBands.Add(band);
        }

        var rows = await _reports.GetInvoiceHistoryAsync(date, date, ct).ConfigureAwait(true);

        Invoices.Clear();
        foreach (var row in rows)
        {
            Invoices.Add(new InvoiceRowViewModel(row));
        }
    }, "Could not load the report.");

    [RelayCommand]
    private void Today() => SelectedDate = DateTime.Today;

    [RelayCommand]
    private void Yesterday() => SelectedDate = DateTime.Today.AddDays(-1);

    [RelayCommand]
    private void PreviousDay() => SelectedDate = SelectedDate.AddDays(-1);

    [RelayCommand]
    private void NextDay() => SelectedDate = SelectedDate.AddDays(1);

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        IsBusyExport = true;
        try
        {
            var date = DateOnly.FromDateTime(SelectedDate);
            var path = await _reports.ExportRangeCsvAsync(date, date).ConfigureAwait(true);
            Notifier.Success($"CSV exported to {path}");
            RevealInExplorer(path);
        }
        catch (Exception ex)
        {
            Notifier.Error($"CSV export failed: {ex.Message}");
        }
        finally
        {
            IsBusyExport = false;
        }
    }

    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        IsBusyExport = true;
        try
        {
            var date = DateOnly.FromDateTime(SelectedDate);
            var report = await _reports.GetDailyReportAsync(date).ConfigureAwait(true);
            var config = await _hardware.GetActiveAsync().ConfigureAwait(true);
            var path = await _pdf.CreateDailyReportPdfAsync(report, config).ConfigureAwait(true);

            Notifier.Success($"PDF report saved to {path}");
            RevealInExplorer(path);
        }
        catch (Exception ex)
        {
            Notifier.Error($"PDF export failed: {ex.Message}");
        }
        finally
        {
            IsBusyExport = false;
        }
    }

    [RelayCommand]
    private void OpenReportsFolder()
    {
        var dir = Data.DatabaseConnectionFactory.ReportsDirectory;
        Directory.CreateDirectory(dir);
        RevealInExplorer(dir);
    }

    private void RevealInExplorer(string path)
    {
        try
        {
            // UseShellExecute is required on .NET Core for the shell to resolve the file association.
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Notifier.Warn($"Could not open Explorer: {ex.Message}");
        }
    }
}
