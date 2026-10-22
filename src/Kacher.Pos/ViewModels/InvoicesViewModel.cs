using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Kacher.Pos.Services;
using Kacher.Pos.Services.Pdf;
using Kacher.Pos.Services.Hardware;
using Kacher.Pos.Services.Repositories;
using Kacher.Pos.ViewModels.Items;

namespace Kacher.Pos.ViewModels;

/// <summary>
/// Invoice history: search any past sale, inspect the full ticket, reprint it or refund it.
/// </summary>
/// <remarks>
/// This is the screen that makes the till usable after the customer has walked away. Refunds are
/// permission-gated (<see cref="Permissions.RefundSale"/>), search is debounced so typing a barcode
/// does not fire a query per keystroke, and every action reports through <see cref="ViewModelBase.Notifier"/>.
/// </remarks>
public sealed partial class InvoicesViewModel : ViewModelBase
{
    private readonly IInvoiceRepository _invoices;
    private readonly IHardwareConfigRepository _hardware;
    private readonly IReceiptPrinter _printer;
    private readonly IPdfReceiptService _pdf;
    private readonly ISessionService _session;
    private CancellationTokenSource? _searchCts;

    /// <summary>In-flight detail query, cancelled when the operator picks another row.</summary>
    private CancellationTokenSource? _detailCts;

    /// <summary>Captured on the UI thread so debounced refreshes can marshal back safely.</summary>
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    public InvoicesViewModel(
        IInvoiceRepository invoices,
        IHardwareConfigRepository hardware,
        IReceiptPrinter printer,
        IPdfReceiptService pdf,
        ISessionService session,
        IUserNotifier notifier) : base(notifier)
    {
        _invoices = invoices;
        _hardware = hardware;
        _printer = printer;
        _pdf = pdf;
        _session = session;

        Title = "Invoices";
    }

    // ── Filter ───────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _todayOnly = true;

    /// <summary>Invoice number of the newest sale, shown as a "live" badge after each refresh.</summary>
    [ObservableProperty]
    private string _lastInvoiceText = string.Empty;

    [ObservableProperty]
    private InvoiceRowViewModel? _selected;

    /// <summary>
    /// Printer reachability, used to grey out "Reprint" instead of letting the operator press a
    /// button that is guaranteed to fail. Refreshed whenever the invoice list is loaded.
    /// </summary>
    [ObservableProperty]
    private bool _printerReady = true;

    /// <summary>Localised one-liner explaining the printer state under the toolbar.</summary>
    [ObservableProperty]
    private string _printerHint = string.Empty;

    [ObservableProperty]
    private InvoiceDetailViewModel? _detail;

    [ObservableProperty]
    private bool _isBusyLoading;

    public bool CanRefund => _session.HasPermission(Permissions.RefundSale);

    public bool CanPrint => _session.HasPermission(Permissions.ViewReports);

    public ObservableCollection<InvoiceRowViewModel> Invoices { get; } = new();

    public bool IsEmpty => Invoices.Count == 0 && !IsBusyLoading;

    // ── Lifecycle ────────────────────────────────────────────────────────────────────────────

    public override Task LoadAsync(CancellationToken ct = default) => RefreshAsync();

    /// <summary>
    /// Debounced so a scan or fast typing results in one query, not twenty. The delay runs on a
    /// background timer and the refresh is posted back through the captured UI context, so the view
    /// model never references a WPF Dispatcher directly.
    /// </summary>
    partial void OnSearchTextChanged(string value) => ScheduleSearch();

    partial void OnTodayOnlyChanged(bool value) => _ = RefreshAsync();

    private void ScheduleSearch()
    {
        var context = _uiContext;

        if (context is null)
        {
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();

        var token = _searchCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, token).ConfigureAwait(false);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                context.Post(
                    _ => _ = RunAsync(ct => RefreshCoreAsync(ct), "Could not search invoices."),
                    null);
            }
            catch (OperationCanceledException)
            {
                // superseded by the next keystroke
            }
        }, token);
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(RefreshCoreAsync, "Could not load invoices.");

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        IsBusyLoading = true;

        try
        {
            var from = TodayOnly ? DateTime.Today.ToUniversalTime() : (DateTime?)null;
            var rows = await _invoices.SearchAsync(SearchText, 200, from, ct).ConfigureAwait(true);

            Invoices.Clear();
            foreach (var row in rows)
            {
                Invoices.Add(new InvoiceRowViewModel(row));
            }

            // Keep the operator's selection if the row is still in the list.
            var selectedId = Selected?.Id;

            if (selectedId is { } id)
            {
                Selected = Invoices.FirstOrDefault(i => i.Id == id);
            }
            else if (Invoices.Count > 0)
            {
                Selected = Invoices[0];
            }

            LastInvoiceText = Invoices.Count > 0
                ? $"Latest: {Invoices[0].InvoiceNumber}"
                : "No invoices yet today";

            OnPropertyChanged(nameof(IsEmpty));
        }
        finally
        {
            IsBusyLoading = false;
        }

        // Probe the printer off the critical path: the list is already on screen, and a dead
        // printer must never delay the screen the manager is looking at.
        _ = ProbePrinterAsync();
    }

    /// <summary>
    /// Asks the printer service whether a device is reachable. Runs on a background thread and
    /// swallows failures — a missing printer is a warning, not an error.
    /// </summary>
    private async Task ProbePrinterAsync()
    {
        try
        {
            var config = await _hardware.GetActiveAsync(CancellationToken.None).ConfigureAwait(true);
            var probe = await _printer.ProbeAsync(config, CancellationToken.None).ConfigureAwait(true);

            PrinterReady = probe.IsAvailable;
            PrinterHint = probe.IsAvailable
                ? $"Printer ready — {config.PrinterName}"
                : probe.Message;
        }
        catch (Exception)
        {
            PrinterReady = false;
            PrinterHint = "Printer check failed.";
        }
    }

    partial void OnSelectedChanged(InvoiceRowViewModel? value)
    {
        // Keyboard-browsing the list can raise this many times per second; cancel the previous
        // detail query so only the last selection ever reaches the database.
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;

        if (value is null)
        {
            Detail = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _detailCts = cts;
        _ = LoadDetailAsync(value.Id, cts);
    }

    private async Task LoadDetailAsync(int invoiceId, CancellationTokenSource cts)
    {
        try
        {
            var invoice = await _invoices.GetByIdAsync(invoiceId, cts.Token).ConfigureAwait(true);

            if (!cts.IsCancellationRequested)
            {
                Detail = invoice is null ? null : new InvoiceDetailViewModel(invoice);
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer selection
        }
        catch (Exception ex)
        {
            Notifier.Error($"Could not load the invoice: {ex.Message}");
        }
    }

    // ── Actions ──────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ClearSearchAsync()
    {
        SearchText = string.Empty;
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ReprintAsync()
    {
        if (Detail is null)
        {
            Notifier.Info("Select an invoice first.");
            return;
        }

        if (Detail.IsRefunded)
        {
            Notifier.Warn("This invoice was refunded — reprint the original or the refund copy.");
        }

        var invoice = Detail.Invoice;

        await RunAsync(async ct =>
        {
            var config = await _hardware.GetActiveAsync(ct).ConfigureAwait(true);

            // A reprint never opens the drawer: the money already moved when the sale happened.
            var result = await _printer
                .PrintSaleReceiptAsync(invoice, config, _session.CurrentUser, _session.CurrentShift, ct)
                .ConfigureAwait(true);

            if (result.Success)
            {
                Notifier.Success($"Reprinted {invoice.InvoiceNumber}.");
            }
            else
            {
                Notifier.Warn($"Printer unavailable: {result.Message}");
            }
        }, "Could not reprint the invoice.").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SavePdfAsync()
    {
        if (Detail is null)
        {
            return;
        }

        await RunAsync(async ct =>
        {
            var config = await _hardware.GetActiveAsync(ct).ConfigureAwait(true);
            var path = await _pdf
                .CreateInvoicePdfAsync(Detail.Invoice, config, Detail.CashierName, ct)
                .ConfigureAwait(true);

            Notifier.Success($"PDF saved: {path}");
        }, "Could not save the PDF.").ConfigureAwait(true);
    }

    /// <summary>
    /// Refunds the whole invoice. The repository writes a mirror (negative) document and returns the
    /// stock, so the Z-report and the stock ledger both stay correct.
    /// </summary>
    [RelayCommand]
    private async Task RefundAsync()
    {
        if (Detail is null)
        {
            Notifier.Info("Select an invoice first.");
            return;
        }

        if (!CanRefund)
        {
            Notifier.Warn("Your role cannot issue refunds.");
            return;
        }

        if (Detail.IsRefunded)
        {
            Notifier.Warn($"{Detail.Invoice.InvoiceNumber} is already refunded.");
            return;
        }

        if (_session.CurrentUser is null)
        {
            Notifier.Warn("Sign in again to refund.");
            return;
        }

        if (!HasDialogs)
        {
            return;
        }

        var confirmed = await Dialogs!.ConfirmAsync(
            "Refund invoice",
            $"Refund {Detail.Invoice.InvoiceNumber} for {Detail.TotalText} and return the items to stock?",
            "Refund").ConfigureAwait(true);

        if (!confirmed)
        {
            return;
        }

        var reason = await Dialogs!.PromptTextAsync("Refund reason", "Why is this sale being refunded?").ConfigureAwait(true);

        await RunAsync(async ct =>
        {
            var refund = await _invoices
                .RefundAsync(Detail.Invoice.Id, _session.CurrentUser.Id, string.IsNullOrWhiteSpace(reason) ? "Customer return" : reason!, ct)
                .ConfigureAwait(true);

            Notifier.Success($"Refunded as {refund.InvoiceNumber}.");
            await RefreshCoreAsync(ct).ConfigureAwait(true);
        }, "Could not refund the invoice.").ConfigureAwait(true);
    }
}

/// <summary>Read-only projection of one invoice for the detail panel.</summary>
public sealed class InvoiceDetailViewModel
{
    public InvoiceDetailViewModel(Invoice invoice)
    {
        Invoice = invoice;
        CashierName = invoice.Cashier?.DisplayName ?? "—";
        CreatedAtLocal = invoice.CreatedAtUtc.ToLocalTime();
        PaymentText = invoice.PaymentMethod.ToString().ToUpperInvariant();
        StatusText = invoice.Status.ToString();
        IsRefunded = invoice.Status == InvoiceStatus.Refunded || invoice.RefundedInvoiceId is not null;

        Lines = invoice.Items
            .Select(item => new InvoiceLineViewModel(
                item.ProductName,
                item.Quantity,
                item.UnitPrice,
                item.DiscountAmount,
                item.TaxAmount,
                item.TotalPrice + item.TaxAmount))
            .ToList();
    }

    public Invoice Invoice { get; }

    /// <summary>Normalised for templates: the row item is only ever bound by name.</summary>
    public string InvoiceNumber => Invoice.InvoiceNumber;

    public string CashierName { get; }

    public DateTime CreatedAtLocal { get; }

    public string PaymentText { get; }

    public string StatusText { get; }

    public bool IsRefunded { get; }

    public string TotalText => $"{Invoice.TotalAmount:0.00} EGP";

    public string SubtotalText => $"{(Invoice.Subtotal + Invoice.DiscountAmount):0.00}";

    public string DiscountText => $"{Invoice.DiscountAmount:0.00}";

    public string TaxText => $"{Invoice.TaxAmount:0.00}";

    public string TenderedText => $"{Invoice.AmountTendered:0.00}";

    public string ChangeText => $"{Invoice.ChangeGiven:0.00}";

    public string? Note => Invoice.Note;

    public IReadOnlyList<InvoiceLineViewModel> Lines { get; }
}

public sealed record InvoiceLineViewModel(
    string Name,
    decimal Quantity,
    decimal UnitPrice,
    decimal Discount,
    decimal Tax,
    decimal LineTotal);
