using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Kacher.Pos.Services;
using Kacher.Pos.Services.Hardware;
using Kacher.Pos.Services.Hardware.Scanner;
using Kacher.Pos.Services.Pdf;
using Kacher.Pos.Services.Repositories;
using Kacher.Pos.ViewModels.Items;

namespace Kacher.Pos.ViewModels;

/// <summary>
/// The till. Owns the cart, catalogue, payment capture and the print hand-off.
///
/// Threading contract:
/// * Every observable collection is mutated on the UI thread only.
/// * Database and printing work is awaited, never blocked; the scanner event (which arrives on an
///   I/O thread) is marshalled through the dispatcher before touching state.
/// * Totals are recomputed from a pure function (<see cref="ISaleService.Calculate"/>) so what the
///   cashier sees and what gets committed are produced by the same code path.
/// </summary>
public sealed partial class SalesViewModel : ViewModelBase, IDisposable
{
    private readonly IProductRepository _products;
    private readonly ISaleService _sales;
    private readonly IReceiptPrinter _printer;
    private readonly IHardwareConfigRepository _hardware;
    private readonly ISessionService _session;
    private readonly IDialogService _dialogs;
    private readonly IPdfReceiptService _pdf;
    private readonly IBarcodeScannerService _scanner;
    private readonly IAppLog _log;

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _scanFlashCts;
    private Invoice? _lastInvoice;
    private bool _suspendRecalc;

    public SalesViewModel(
        IProductRepository products,
        ISaleService sales,
        IReceiptPrinter printer,
        IHardwareConfigRepository hardware,
        ISessionService session,
        IDialogService dialogs,
        IPdfReceiptService pdf,
        IBarcodeScannerService scanner,
        IAppLog log,
        IUserNotifier notifier) : base(notifier)
    {
        _products = products;
        _sales = sales;
        _printer = printer;
        _hardware = hardware;
        _session = session;
        _dialogs = dialogs;
        _pdf = pdf;
        _scanner = scanner;
        _log = log;

        Title = "Sales";
        _scanner.BarcodeScanned += OnBarcodeScanned;
    }

    // ── State ────────────────────────────────────────────────────────────────────────────────

    public ObservableCollection<ProductTileViewModel> Products { get; } = new();

    public ObservableCollection<CategoryTabViewModel> Categories { get; } = new();

    public ObservableCollection<CartLineViewModel> Cart { get; } = new();

    public ObservableCollection<decimal> TenderSuggestions { get; } = new();

    [ObservableProperty]
    private CategoryTabViewModel? _selectedCategory;

    [ObservableProperty]
    private CartLineViewModel? _selectedLine;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Always focused scanner/search field. A keyboard-wedge scan lands here directly.</summary>
    [ObservableProperty]
    private string _barcodeInput = string.Empty;

    [ObservableProperty]
    private decimal _subtotal;

    [ObservableProperty]
    private decimal _taxAmount;

    [ObservableProperty]
    private decimal _discountAmount;

    [ObservableProperty]
    private decimal _total;

    [ObservableProperty]
    private decimal _invoiceDiscount;

    [ObservableProperty]
    private int _itemCount;

    [ObservableProperty]
    private string _scanMessage = string.Empty;

    [ObservableProperty]
    private bool _isScanSuccess;

    [ObservableProperty]
    private string? _lastScannedName;

    // ── Checkout ─────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isCheckoutOpen;

    [ObservableProperty]
    private PaymentMethod _paymentMethod = PaymentMethod.Cash;

    [ObservableProperty]
    private string _tenderedText = string.Empty;

    [ObservableProperty]
    private decimal _changeDue;

    [ObservableProperty]
    private bool _isCashSelected = true;

    [ObservableProperty]
    private bool _isCardSelected;

    [ObservableProperty]
    private string? _lastReceiptNumber;

    [ObservableProperty]
    private bool _lastSaleHasReceipt;

    [ObservableProperty]
    private string _cartEmptyHint = "Cart is empty — scan a barcode or tap a product.";

    public bool IsCartEmpty => Cart.Count == 0;

    public bool HasDiscount => DiscountAmount > 0;

    /// <summary>Gate for the payment buttons: needs items, an open shift and no in-flight operation.</summary>
    public bool CanPay => !IsCartEmpty && !IsBusy;

    public string CashierName => _session.CashierName;

    public string ShiftLabel => _session.CurrentShift is { } shift
        ? $"Shift {shift.ShiftNumber}"
        : "No open shift";

    [ObservableProperty]
    private string _invoicePreviewNumber = "New sale";

    // ── Lifecycle ────────────────────────────────────────────────────────────────────────────

    public override async Task LoadAsync(CancellationToken ct = default)
    {
        await RunAsync(async token =>
        {
            var categories = await _products.GetCategoriesAsync(token).ConfigureAwait(true);
            var catalogue = await _products.GetCatalogueAsync(null, null, false, token).ConfigureAwait(true);

            Categories.Clear();
            Categories.Add(CategoryTabViewModel.All);
            foreach (var category in categories)
            {
                Categories.Add(CategoryTabViewModel.FromModel(category,
                    catalogue.Count(p => p.CategoryId == category.Id)));
            }

            ReplaceProducts(catalogue);

            if (SelectedCategory is null)
            {
                SelectedCategory = Categories.FirstOrDefault();
            }
        }, "Could not load the catalogue.");
    }

    public override async Task UnloadAsync()
    {
        _searchCts?.Cancel();
        _scanFlashCts?.Cancel();
        await Task.CompletedTask;
    }

    private void ReplaceProducts(IReadOnlyList<Product> catalogue)
    {
        Products.Clear();
        foreach (var product in catalogue)
        {
            Products.Add(new ProductTileViewModel(product));
        }
    }

    // ── Catalogue filtering ──────────────────────────────────────────────────────────────────

    partial void OnSelectedCategoryChanged(CategoryTabViewModel? value)
    {
        _ = ReloadCatalogueAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        // Debounce: a touch keyboard fires ~30 changes/s; each one must not become a DB query.
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();

        var token = _searchCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(220, token).ConfigureAwait(false);
                await ReloadCatalogueAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // superseded by a newer keystroke
            }
        }, token);
    }

    private async Task ReloadCatalogueAsync(CancellationToken ct = default)
    {
        var categoryId = SelectedCategory is { Id: > 0 } chosen ? chosen.Id : (int?)null;
        var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();

        var catalogue = await _products.GetCatalogueAsync(categoryId, search, false, ct).ConfigureAwait(true);

        if (ct.IsCancellationRequested)
        {
            return;
        }

        ReplaceProducts(catalogue);
    }

    [RelayCommand]
    private Task RefreshCatalogueAsync() => RunAsync(_ => ReloadCatalogueAsync(), "Could not refresh the catalogue.");

    // ── Cart operations ──────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void AddProduct(ProductTileViewModel? tile)
    {
        if (tile is null || !tile.CanSell)
        {
            if (tile is { IsOutOfStock: true })
            {
                Notifier.Warn($"{tile.Name} is out of stock.");
            }
            return;
        }

        AddToCart(tile.Model);
        FlashScan($"+ {tile.Name}", success: true);
    }

    private void AddToCart(Product product, decimal quantity = 1m)
    {
        var existing = Cart.FirstOrDefault(line =>
            line.ProductId == product.Id && line.DiscountAmount == 0m && !line.IsWeighted);

        if (existing is not null)
        {
            existing.Quantity += quantity;
        }
        else
        {
            var line = new CartLineViewModel(product, quantity);
            line.PropertyChanged += OnCartLineChanged;
            Cart.Add(line);
        }

        RecalculateTotals();
        OnPropertyChanged(nameof(IsCartEmpty));
    }

    private void OnCartLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CartLineViewModel.Quantity) or nameof(CartLineViewModel.DiscountAmount))
        {
            RecalculateTotals();
        }
    }

    [RelayCommand]
    private void IncreaseQuantity(CartLineViewModel? line)
    {
        if (line is null)
        {
            return;
        }

        line.Quantity += line.IsWeighted ? 0.5m : 1m;
    }

    [RelayCommand]
    private void DecreaseQuantity(CartLineViewModel? line)
    {
        if (line is null)
        {
            return;
        }

        var step = line.IsWeighted ? 0.5m : 1m;

        if (line.Quantity - step <= 0)
        {
            RemoveLine(line);
            return;
        }

        line.Quantity -= step;
    }

    [RelayCommand]
    private void RemoveLine(CartLineViewModel? line)
    {
        if (line is null)
        {
            return;
        }

        line.PropertyChanged -= OnCartLineChanged;
        Cart.Remove(line);
        RecalculateTotals();
        OnPropertyChanged(nameof(IsCartEmpty));
    }

    [RelayCommand]
    private async Task ClearCartAsync()
    {
        if (Cart.Count == 0)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Clear cart", "Remove all items from this sale?", "Clear cart").ConfigureAwait(true))
        {
            return;
        }

        foreach (var line in Cart)
        {
            line.PropertyChanged -= OnCartLineChanged;
        }

        Cart.Clear();
        InvoiceDiscount = 0m;
        RecalculateTotals();
        OnPropertyChanged(nameof(IsCartEmpty));
    }

    [RelayCommand]
    private async Task EditLineDiscountAsync(CartLineViewModel? line)
    {
        if (line is null)
        {
            return;
        }

        if (!_session.HasPermission(Permissions.ApplyDiscount))
        {
            Notifier.Warn("Your role cannot apply discounts. Ask a supervisor.");
            return;
        }

        var input = await _dialogs.PromptAmountAsync(
            $"Discount — {line.Name}",
            "Line discount amount (EGP)",
            line.DiscountAmount,
            "Apply").ConfigureAwait(true);

        if (input is null)
        {
            return;
        }

        if (!decimal.TryParse(input, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            return;
        }

        var gross = line.Quantity * line.UnitPrice;
        line.DiscountAmount = Math.Clamp(amount, 0m, gross);
        RecalculateTotals();
    }

    [RelayCommand]
    private async Task ApplyInvoiceDiscountAsync()
    {
        if (!_session.HasPermission(Permissions.ApplyDiscount))
        {
            Notifier.Warn("Your role cannot apply discounts. Ask a supervisor.");
            return;
        }

        if (Cart.Count == 0)
        {
            return;
        }

        var input = await _dialogs.PromptAmountAsync(
            "Order discount",
            "Discount on the whole sale (EGP)",
            InvoiceDiscount,
            "Apply").ConfigureAwait(true);

        if (input is null)
        {
            return;
        }

        if (decimal.TryParse(input, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            InvoiceDiscount = Math.Max(0m, amount);
        }
    }

    [RelayCommand]
    private void ClearInvoiceDiscount()
    {
        InvoiceDiscount = 0m;
    }

    partial void OnInvoiceDiscountChanged(decimal value) => RecalculateTotals();

    partial void OnTenderedTextChanged(string value)
    {
        var tendered = ParseMoney(value);
        ChangeDue = Math.Round(Math.Max(0m, tendered - Total), 2, MidpointRounding.AwayFromZero);
        ChangeDisplay = ChangeDue.ToString("0.00");
    }

    [ObservableProperty]
    private string _changeDisplay = "0.00";

    /// <summary>
    /// Single source of truth for the money shown in the bottom bar. Runs a pure calculation, so it
    /// is safe to call on every cart mutation and every tender keystroke.
    /// </summary>
    private void RecalculateTotals()
    {
        if (_suspendRecalc)
        {
            return;
        }

        var requests = Cart.Select(line => line.ToRequest()).ToList();
        var totals = _sales.Calculate(requests, InvoiceDiscount);

        Subtotal = totals.Subtotal;
        TaxAmount = totals.TaxAmount;
        DiscountAmount = totals.LineDiscount + totals.InvoiceDiscount;
        Total = totals.Total;
        // Ceiling keeps a 1.5 kg weighted line visible as "2 items" rather than 1.
        ItemCount = (int)Math.Ceiling(Cart.Sum(line => line.Quantity));

        // Refresh the cash quick-tender chips whenever the amount due moves.
        TenderSuggestions.Clear();
        foreach (var suggestion in CashRounding.SuggestedTenders(Total))
        {
            TenderSuggestions.Add(suggestion);
        }

        if (IsCheckoutOpen && PaymentMethod == PaymentMethod.Cash)
        {
            TenderedText = Total.ToString("0.00", CultureInfo.InvariantCulture);
        }

        OnPropertyChanged(nameof(HasDiscount));
        OnPropertyChanged(nameof(CanPay));
    }

    /// <summary>Keeps <see cref="CanPay"/> reactive to the busy flag raised by <see cref="ViewModelBase"/>.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(IsBusy))
        {
            OnPropertyChanged(nameof(CanPay));
        }
    }

    // ── Barcode scanning ─────────────────────────────────────────────────────────────────────

    private void OnBarcodeScanned(object? sender, BarcodeScannedEventArgs e)
    {
        // The scanner raises from an I/O thread. Marshal before touching any observable state.
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            _ = HandleBarcodeAsync(e.Barcode);
            return;
        }

        dispatcher.BeginInvoke(new Action(() => _ = HandleBarcodeAsync(e.Barcode)));
    }

    private async Task HandleBarcodeAsync(string barcode)
    {
        try
        {
            var product = await _products.FindByBarcodeAsync(barcode).ConfigureAwait(true);

            // Fallback to the short SKU so hand-typed "COLA" codes also resolve.
            product ??= (await _products.GetCatalogueAsync(null, barcode, false).ConfigureAwait(true))
                .FirstOrDefault(p => string.Equals(p.Sku, barcode, StringComparison.OrdinalIgnoreCase));

            if (product is null)
            {
                IsScanSuccess = false;
                FlashScan($"Unknown barcode: {barcode}", success: false);
                BarcodeInput = string.Empty;
                return;
            }

            if (!product.IsActive)
            {
                IsScanSuccess = false;
                FlashScan($"{product.Name} is not for sale", success: false);
                return;
            }

            AddToCart(product);

            LastScannedName = product.Name;
            FlashScan($"{product.Name} added", success: true);

            // Clear the scan field so a second scan of the same code is not appended to the first.
            BarcodeInput = string.Empty;
        }
        catch (Exception ex)
        {
            _log.Error($"Barcode lookup failed for '{barcode}'.", ex);
            Notifier.Error("Could not look up that barcode.");
        }
    }

    /// <summary>Enter pressed in the scan/search field.</summary>
    [RelayCommand]
    private async Task SubmitBarcodeAsync()
    {
        var code = BarcodeInput.Trim();

        if (string.IsNullOrEmpty(code))
        {
            return;
        }

        BarcodeInput = string.Empty;

        // Digits-only and long enough → treat as a barcode; otherwise it is a text search.
        if (code.Length >= 6 && code.All(char.IsDigit))
        {
            await HandleBarcodeAsync(code).ConfigureAwait(true);
            return;
        }

        SearchText = code;
    }

    [RelayCommand]
    private void ClearScanFeedback()
    {
        ScanMessage = string.Empty;
        LastScannedName = null;
    }

    private void FlashScan(string message, bool success)
    {
        ScanMessage = message;
        IsScanSuccess = success;

        _scanFlashCts?.Cancel();
        _scanFlashCts = new CancellationTokenSource();
        var token = _scanFlashCts.Token;

        // Auto-dismiss the banner. Fire-and-forget on the UI context is safe because the CTS is
        // cancelled on navigation and the handler only touches bound properties.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1600), token).ConfigureAwait(false);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        ScanMessage = string.Empty;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // another scan superseded this banner
            }
        }, token);
    }

    // ── Checkout ─────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenCashCheckout()
    {
        if (!CanStartCheckout())
        {
            return;
        }

        PaymentMethod = PaymentMethod.Cash;
        IsCashSelected = true;
        IsCardSelected = false;
        TenderedText = Total.ToString("0.00", CultureInfo.InvariantCulture);
        ChangeDue = 0m;
        ChangeDisplay = "0.00";
        IsCheckoutOpen = true;
    }

    [RelayCommand]
    private void OpenCardCheckout()
    {
        if (!CanStartCheckout())
        {
            return;
        }

        PaymentMethod = PaymentMethod.Card;
        IsCashSelected = false;
        IsCardSelected = true;
        TenderedText = Total.ToString("0.00", CultureInfo.InvariantCulture);
        ChangeDue = 0m;
        ChangeDisplay = "0.00";
        IsCheckoutOpen = true;
    }

    private bool CanStartCheckout()
    {
        if (Cart.Count == 0)
        {
            Notifier.Warn("Add at least one item before taking payment.");
            return false;
        }

        // A sale must belong to a drawer session, otherwise the Z-report cannot reconcile it.
        if (!_session.ShiftIsOpen)
        {
            Notifier.Warn("No shift is open. Open a shift before taking payment.");
            return false;
        }

        return true;
    }

    [RelayCommand]
    private void CloseCheckout()
    {
        IsCheckoutOpen = false;
    }

    [RelayCommand]
    private void SelectCash()
    {
        PaymentMethod = PaymentMethod.Cash;
        IsCashSelected = true;
        IsCardSelected = false;
        TenderedText = Total.ToString("0.00", CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private void SelectCard()
    {
        PaymentMethod = PaymentMethod.Card;
        IsCashSelected = false;
        IsCardSelected = true;
        TenderedText = Total.ToString("0.00", CultureInfo.InvariantCulture);
        ChangeDue = 0m;
        ChangeDisplay = "0.00";
    }

    /// <summary>Touch keypad. Accepts digits and one decimal separator.</summary>
    [RelayCommand]
    private void AppendTenderKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || PaymentMethod != PaymentMethod.Cash)
        {
            return;
        }

        if (key == ".")
        {
            if (!TenderedText.Contains('.'))
            {
                TenderedText = string.IsNullOrEmpty(TenderedText) ? "0." : TenderedText + ".";
            }
            return;
        }

        if (!key.All(char.IsDigit))
        {
            return;
        }

        // Guard against nonsense amounts (9999999.99) — protects the change calculation too.
        var candidate = TenderedText == "0" ? key : TenderedText + key;
        if (candidate.Length > 9)
        {
            return;
        }

        TenderedText = candidate;
    }

    [RelayCommand]
    private void BackspaceTender()
    {
        if (!string.IsNullOrEmpty(TenderedText))
        {
            TenderedText = TenderedText[..^1];
        }
    }

    [RelayCommand]
    private void ClearTender() => TenderedText = string.Empty;

    [RelayCommand]
    private void UseExactTender() => TenderedText = Total.ToString("0.00", CultureInfo.InvariantCulture);

    [RelayCommand]
    private void UseSuggestion(object? suggestion)
    {
        var amount = suggestion switch
        {
            decimal d => d,
            string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0m
        };

        if (amount > 0)
        {
            TenderedText = amount.ToString("0.00", CultureInfo.InvariantCulture);
        }
    }

    [RelayCommand]
    private async Task CompletePaymentAsync()
    {
        if (!_session.ShiftIsOpen)
        {
            Notifier.Warn("No shift is open — payment cannot be recorded.");
            return;
        }

        var payments = new List<PaymentRequest>();
        var tendered = PaymentMethod == PaymentMethod.Cash ? ParseMoney(TenderedText) : Total;

        if (PaymentMethod == PaymentMethod.Cash && tendered + 0.005m < Total)
        {
            Notifier.Warn($"Cash tendered ({tendered:0.00}) is less than the total ({Total:0.00}).");
            return;
        }

        payments.Add(new PaymentRequest(
            PaymentMethod,
            PaymentMethod == PaymentMethod.Cash ? Math.Round(tendered, 2) : Total,
            PaymentMethod == PaymentMethod.Card ? $"CARD-{DateTime.Now:HHmmss}" : null));

        var request = new SaleRequest(
            CashierId: _session.CurrentUser?.Id ?? 0,
            ShiftId: _session.CurrentShift?.Id,
            Lines: Cart.Select(line => line.ToRequest()).ToList(),
            InvoiceDiscount: InvoiceDiscount,
            Payments: payments,
            Note: null);

        await RunAsync(async ct =>
        {
            var invoice = await _sales.CommitAsync(request, ct).ConfigureAwait(true);
            _lastInvoice = invoice;
            LastReceiptNumber = invoice.InvoiceNumber;
            LastSaleHasReceipt = true;
            InvoicePreviewNumber = invoice.InvoiceNumber;

            // Reset the till *before* printing: a jammed printer must not hold the next customer.
            ResetCartAfterSale();
            IsCheckoutOpen = false;

            await PrintReceiptAsync(invoice, ct).ConfigureAwait(true);

            Notifier.Success($"Sale {invoice.InvoiceNumber} completed · {invoice.TotalAmount:0.00} EGP");

            // Stock levels changed — refresh the visible catalogue without dropping the filter.
            await ReloadCatalogueAsync(ct).ConfigureAwait(true);
        }, "The sale could not be completed.").ConfigureAwait(true);
    }

    private void ResetCartAfterSale()
    {
        _suspendRecalc = true;

        foreach (var line in Cart)
        {
            line.PropertyChanged -= OnCartLineChanged;
        }

        Cart.Clear();
        InvoiceDiscount = 0m;
        TenderedText = string.Empty;
        ChangeDue = 0m;
        ChangeDisplay = "0.00";

        _suspendRecalc = false;
        RecalculateTotals();
        OnPropertyChanged(nameof(IsCartEmpty));
    }

    /// <summary>
    /// Printing never blocks the UI and never fails a committed sale. If the printer is unreachable
    /// the receipt is written to PDF and the operator is told where to find it.
    /// </summary>
    private async Task PrintReceiptAsync(Invoice invoice, CancellationToken ct)
    {
        try
        {
            var config = await _hardware.GetActiveAsync(ct).ConfigureAwait(true);
            var result = await _printer
                .PrintSaleReceiptAsync(invoice, config, _session.CurrentUser, _session.CurrentShift, ct)
                .ConfigureAwait(true);

            if (result.Success)
            {
                return;
            }

            Notifier.Warn($"Receipt not printed: {result.Message}");

            var pdfPath = await _pdf.CreateInvoicePdfAsync(invoice, config, _session.CashierName, ct).ConfigureAwait(true);
            Notifier.Info($"Receipt saved as PDF: {pdfPath}");
        }
        catch (Exception ex)
        {
            _log.Error("Receipt printing failed after a committed sale.", ex);
            Notifier.Warn("Receipt could not be printed. The sale is saved.");
        }
    }

    [RelayCommand]
    private async Task ReprintLastAsync()
    {
        if (_lastInvoice is null)
        {
            Notifier.Info("There is no receipt to reprint yet.");
            return;
        }

        await RunAsync(async ct =>
        {
            var config = await _hardware.GetActiveAsync(ct).ConfigureAwait(true);
            var result = await _printer
                .PrintSaleReceiptAsync(_lastInvoice, config, _session.CurrentUser, _session.CurrentShift, ct)
                .ConfigureAwait(true);

            if (result.Success)
            {
                Notifier.Success($"Reprinted {_lastInvoice.InvoiceNumber}.");
            }
            else
            {
                Notifier.Warn($"Reprint failed: {result.Message}");
            }
        }, "Reprint failed.").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveReceiptPdfAsync()
    {
        if (_lastInvoice is null)
        {
            Notifier.Info("No receipt to save yet.");
            return;
        }

        await RunAsync(async ct =>
        {
            var config = await _hardware.GetActiveAsync(ct).ConfigureAwait(true);
            var path = await _pdf.CreateInvoicePdfAsync(_lastInvoice, config, _session.CashierName, ct).ConfigureAwait(true);
            Notifier.Success($"Receipt PDF saved: {path}");
        }, "Could not save the receipt PDF.").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenDrawerAsync()
    {
        if (!_session.HasPermission(Permissions.OpenDrawer))
        {
            Notifier.Warn("Your role cannot open the cash drawer.");
            return;
        }

        await RunAsync(async ct =>
        {
            var config = await _hardware.GetActiveAsync(ct).ConfigureAwait(true);
            var result = await _printer.OpenDrawerAsync(config, ct).ConfigureAwait(true);

            if (result.Success)
            {
                Notifier.Success("Drawer pulse sent.");
            }
            else
            {
                Notifier.Error($"Drawer did not open: {result.Message}");
            }
        }, "Could not open the drawer.").ConfigureAwait(true);
    }

    /// <summary>Manual quantity entry — handy for deli items sold by weight.</summary>
    [RelayCommand]
    private async Task SetQuantityAsync(CartLineViewModel? line)
    {
        if (line is null)
        {
            return;
        }

        var input = await _dialogs.PromptAmountAsync(
            $"Quantity — {line.Name}",
            line.IsWeighted ? "Weight in kg" : "Quantity",
            line.Quantity,
            "Set").ConfigureAwait(true);

        if (input is null)
        {
            return;
        }

        if (decimal.TryParse(input, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty) && qty > 0)
        {
            line.Quantity = qty;
        }
    }

    private static decimal ParseMoney(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0m;
        }

        // Accept both "12.50" and "12,50" — some numeric keypads emit a comma.
        var normalised = text.Replace(',', '.');
        return decimal.TryParse(normalised, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? Math.Round(value, 2, MidpointRounding.AwayFromZero)
            : 0m;
    }

    public void Dispose()
    {
        _scanner.BarcodeScanned -= OnBarcodeScanned;
        _searchCts?.Cancel();
        _scanFlashCts?.Cancel();
    }
}
