using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Kacher.Pos.Services;
using Kacher.Pos.Services.Hardware;
using Kacher.Pos.Services.Pdf;
using Kacher.Pos.Services.Repositories;

namespace Kacher.Pos.ViewModels;

/// <summary>
/// End-of-day drawer control: open a shift with a float, watch live totals, count the drawer,
/// close the shift and emit the Z-report (paper and/or PDF).
///
/// The closing arithmetic lives in <see cref="ShiftService"/>; this view model only presents it, so
/// the same numbers are guaranteed on screen, on paper and in the stored JSON snapshot.
/// </summary>
public sealed partial class ShiftViewModel : ViewModelBase
{
    private readonly IShiftService _shifts;
    private readonly ISessionService _session;
    private readonly IDialogService _dialogs;
    private readonly IReceiptPrinter _printer;
    private readonly IHardwareConfigRepository _hardware;
    private readonly IPdfReceiptService _pdf;

    public ShiftViewModel(
        IShiftService shifts,
        ISessionService session,
        IDialogService dialogs,
        IReceiptPrinter printer,
        IHardwareConfigRepository hardware,
        IPdfReceiptService pdf,
        IUserNotifier notifier) : base(notifier)
    {
        _shifts = shifts;
        _session = session;
        _dialogs = dialogs;
        _printer = printer;
        _hardware = hardware;
        _pdf = pdf;

        Title = "Shift & End of Day";
    }

    [ObservableProperty]
    private bool _hasOpenShift;

    [ObservableProperty]
    private string _shiftNumber = "—";

    [ObservableProperty]
    private string _cashierName = "—";

    [ObservableProperty]
    private DateTime? _openedAt;

    [ObservableProperty]
    private string _openingFloatText = "0.00";

    [ObservableProperty]
    private string _countedCashText = "0.00";

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
    private decimal _openingFloat;

    [ObservableProperty]
    private decimal _paidIn;

    [ObservableProperty]
    private decimal _paidOut;

    [ObservableProperty]
    private decimal _expectedCash;

    [ObservableProperty]
    private decimal _countedCash;

    [ObservableProperty]
    private decimal _cashDifference;

    [ObservableProperty]
    private string _closingNote = string.Empty;

    [ObservableProperty]
    private ShiftSummary? _summary;

    [ObservableProperty]
    private bool _isClosed;

    partial void OnHasOpenShiftChanged(bool value)
    {
        OnPropertyChanged(nameof(CanOpenShift));
        OnPropertyChanged(nameof(CanCloseShift));
        OnPropertyChanged(nameof(IntegrityHint));
    }

    public bool CanOpenShift => !HasOpenShift && _session.IsSignedIn;

    public bool CanCloseShift => HasOpenShift && _session.HasPermission(Permissions.CloseShift);

    public string IntegrityHint => HasOpenShift
        ? "Cash drawer is OPEN — sales are being recorded against this shift."
        : "No shift open. Sales are blocked until a shift is started.";

    public override Task LoadAsync(CancellationToken ct = default) =>
        RunAsync(RefreshCoreAsync, "Could not load shift data.");

    /// <summary>
    /// Unguarded reload. Commands that already run inside a <see cref="ViewModelBase.RunAsync"/>
    /// block (open / close / cash movement) call this directly, otherwise the busy guard would
    /// silently swallow the refresh.
    /// </summary>
    private async Task RefreshCoreAsync(CancellationToken token)
    {
        var shift = await _shifts.GetOpenShiftAsync(token).ConfigureAwait(true);
        _session.SetShift(shift);

        if (shift is null)
        {
            HasOpenShift = false;
            IsClosed = false;
            Summary = null;
            ResetTotals();
            return;
        }

        HasOpenShift = true;
        IsClosed = false;
        ShiftNumber = shift.ShiftNumber;
        CashierName = shift.User?.DisplayName ?? _session.CashierName;
        OpenedAt = shift.OpenedAtUtc.ToLocalTime();
        OpeningFloatText = shift.OpeningFloat.ToString("0.00");

        var summary = await _shifts
            .BuildSummaryAsync(shift.Id, ParseMoney(CountedCashText), token)
            .ConfigureAwait(true);

        ApplySummary(summary);
    }

    private void ResetTotals()
    {
        InvoiceCount = 0;
        GrossSales = NetSales = TaxTotal = DiscountTotal = 0m;
        CashTotal = CardTotal = RefundTotal = OpeningFloat = PaidIn = PaidOut = 0m;
        ExpectedCash = CountedCash = CashDifference = 0m;
    }

    private void ApplySummary(ShiftSummary summary)
    {
        Summary = summary;
        ShiftNumber = summary.ShiftNumber;
        CashierName = summary.CashierName;
        OpenedAt = summary.OpenedAt;
        InvoiceCount = summary.InvoiceCount;
        GrossSales = summary.GrossSales;
        NetSales = summary.NetSales;
        TaxTotal = summary.TaxTotal;
        DiscountTotal = summary.DiscountTotal;
        CashTotal = summary.CashTotal;
        CardTotal = summary.CardTotal;
        RefundTotal = summary.RefundTotal;
        OpeningFloat = summary.OpeningFloat;
        PaidIn = summary.PaidIn;
        PaidOut = summary.PaidOut;
        ExpectedCash = summary.ExpectedCash;
        CountedCash = summary.CountedCash;
        CashDifference = summary.CashDifference;
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(RefreshCoreAsync, "Could not load shift data.");

    [RelayCommand]
    private async Task OpenShiftAsync()
    {
        if (!_session.IsSignedIn)
        {
            Notifier.Warn("Sign in first.");
            return;
        }

        if (HasOpenShift)
        {
            Notifier.Warn("A shift is already open.");
            return;
        }

        var input = await _dialogs
            .PromptAmountAsync("Open shift", "Opening cash float (EGP)", 0m, "Open shift")
            .ConfigureAwait(true);

        if (input is null)
        {
            return;
        }

        OpeningFloatText = input;

        await RunAsync(async ct =>
        {
            var shift = await _shifts
                .OpenShiftAsync(_session.CurrentUser!.Id, ParseMoney(input), ct)
                .ConfigureAwait(true);

            _session.SetShift(shift);
            HasOpenShift = true;
            IsClosed = false;
            Notifier.Success($"Shift {shift.ShiftNumber} opened.");
            await RefreshCoreAsync(ct).ConfigureAwait(true);
        }, "Could not open the shift.");
    }

    [RelayCommand]
    private async Task CloseShiftAsync()
    {
        if (!HasOpenShift || Summary is null)
        {
            return;
        }

        if (!_session.HasPermission(Permissions.CloseShift))
        {
            Notifier.Warn("Only a supervisor or admin can close a shift.");
            return;
        }

        var counted = await _dialogs
            .PromptAmountAsync("Count the drawer", "Cash counted in the drawer (EGP)", ExpectedCash, "Count")
            .ConfigureAwait(true);

        if (counted is null)
        {
            return;
        }

        CountedCashText = counted;
        CountedCash = ParseMoney(counted);

        // An unexplained difference over 50 EGP is a hard stop for a cashier — it needs a note.
        if (Math.Abs(CountedCash - ExpectedCash) > 50m && string.IsNullOrWhiteSpace(ClosingNote))
        {
            var note = await _dialogs
                .PromptTextAsync("Explain the difference",
                    $"Expected {ExpectedCash:0.00}, counted {CountedCash:0.00}. Reason for the variance:",
                    null, "Continue")
                .ConfigureAwait(true);

            if (note is null)
            {
                return;
            }

            ClosingNote = note;
        }

        if (!await _dialogs
                .ConfirmAsync("Close shift", $"Close {ShiftNumber} and print the Z-report?", "Close & print")
                .ConfigureAwait(true))
        {
            return;
        }

        await RunAsync(async ct =>
        {
            var summary = await _shifts
                .CloseShiftAsync(Summary.ShiftId, CountedCash, ClosingNote, ct)
                .ConfigureAwait(true);

            ApplySummary(summary);
            HasOpenShift = false;
            IsClosed = true;
            _session.SetShift(null);

            Notifier.Success($"Shift closed. Difference {summary.CashDifference:0.00} EGP.");

            await PrintZReportInternalAsync(summary, ct).ConfigureAwait(true);
        }, "Could not close the shift.");
    }

    [RelayCommand]
    private async Task AddPaidInAsync()
    {
        if (Summary is null)
        {
            return;
        }

        var input = await _dialogs.PromptAmountAsync("Paid in", "Cash added to the drawer (EGP)", 0m, "Add").ConfigureAwait(true);

        if (input is null)
        {
            return;
        }

        await RecordMovementAsync(CashMovementKind.PaidIn, ParseMoney(input), "Manual paid-in").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AddPaidOutAsync()
    {
        if (Summary is null)
        {
            return;
        }

        var input = await _dialogs.PromptAmountAsync("Paid out", "Cash removed from the drawer (EGP)", 0m, "Remove").ConfigureAwait(true);

        if (input is null)
        {
            return;
        }

        var reason = await _dialogs
            .PromptTextAsync("Paid out", "Reason (e.g. supplier, bank drop)", "Bank drop", "Save")
            .ConfigureAwait(true);

        await RecordMovementAsync(CashMovementKind.PaidOut, ParseMoney(input), reason ?? "Manual paid-out")
            .ConfigureAwait(true);
    }

    private async Task RecordMovementAsync(CashMovementKind kind, decimal amount, string reason)
    {
        if (Summary is null || amount <= 0)
        {
            return;
        }

        await RunAsync(async ct =>
        {
            await _shifts.AddCashMovementAsync(Summary.ShiftId, kind, amount, reason, ct).ConfigureAwait(true);
            await RefreshCoreAsync(ct).ConfigureAwait(true);
            Notifier.Success($"{kind} of {amount:0.00} EGP recorded.");
        }, "Could not record the cash movement.");
    }

    [RelayCommand]
    private async Task PrintZReportAsync()
    {
        if (Summary is null)
        {
            Notifier.Info("There is no shift summary to print.");
            return;
        }

        await RunAsync(async ct =>
        {
            var summary = await _shifts.BuildSummaryAsync(Summary.ShiftId, CountedCash, ct).ConfigureAwait(true);
            await PrintZReportInternalAsync(summary, ct).ConfigureAwait(true);
        }, "Could not print the Z-report.");
    }

    private async Task PrintZReportInternalAsync(ShiftSummary summary, CancellationToken ct)
    {
        var config = await _hardware.GetActiveAsync(ct).ConfigureAwait(true);
        var result = await _printer.PrintZReportAsync(summary, config, ct).ConfigureAwait(true);

        if (result.Success)
        {
            Notifier.Success("Z-report sent to the printer.");
            return;
        }

        Notifier.Warn($"Printer unavailable ({result.Message}). Saving a PDF instead.");

        var path = await _pdf.CreateShiftSummaryPdfAsync(summary, config, ct).ConfigureAwait(true);
        Notifier.Info($"Z-report PDF saved to {path}");
    }

    [RelayCommand]
    private async Task ExportZReportPdfAsync()
    {
        if (Summary is null)
        {
            return;
        }

        await RunAsync(async ct =>
        {
            var config = await _hardware.GetActiveAsync(ct).ConfigureAwait(true);
            var path = await _pdf.CreateShiftSummaryPdfAsync(Summary, config, ct).ConfigureAwait(true);
            Notifier.Success($"Z-report PDF saved to {path}");
        }, "Could not export the Z-report.");
    }

    private static decimal ParseMoney(string? value) =>
        decimal.TryParse(
            (value ?? string.Empty).Replace(',', '.'),
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0m;
}
