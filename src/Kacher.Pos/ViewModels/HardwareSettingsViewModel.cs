using System.Collections.ObjectModel;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Kacher.Pos.Services;
using Kacher.Pos.Services.Hardware;
using Kacher.Pos.Services.Hardware.EscPos;
using Kacher.Pos.Services.Hardware.Transports;
using Kacher.Pos.Services.Repositories;

namespace Kacher.Pos.ViewModels;

public sealed record CodePageOption(byte Index, string Name)
{
    public override string ToString() => $"{Index} — {Name}";
}

/// <summary>
/// Hardware commissioning screen. Every field maps 1:1 to a persisted <see cref="HardwareConfig"/>
/// column, so what the technician tests is exactly what the till will use.
///
/// Test operations are the interesting part: "test print" exercises the whole stack (encoding →
/// layout → transport), while "test drawer" sends only the configured kick pulse, which is the
/// correct way to isolate a drawer fault from a printer fault.
/// </summary>
public sealed partial class HardwareSettingsViewModel : ViewModelBase
{
    private readonly IHardwareConfigRepository _configs;
    private readonly ThermalTransportFactory _transports;
    private readonly IReceiptPrinter _printer;
    private readonly ISessionService _session;

    public HardwareSettingsViewModel(
        IHardwareConfigRepository configs,
        ThermalTransportFactory transports,
        IReceiptPrinter printer,
        ISessionService session,
        IUserNotifier notifier) : base(notifier)
    {
        _configs = configs;
        _transports = transports;
        _printer = printer;
        _session = session;

        Title = "Hardware";

        ConnectionTypes = new ObservableCollection<PrinterConnectionType>(Enum.GetValues<PrinterConnectionType>());
        PaperWidths = new ObservableCollection<PaperWidth>(Enum.GetValues<PaperWidth>());
        DrawerTypes = new ObservableCollection<DrawerConnectionType>(Enum.GetValues<DrawerConnectionType>());
        BaudRates = new ObservableCollection<int> { 9600, 19200, 38400, 57600, 115200 };
        CodePages = new ObservableCollection<CodePageOption>(
            EscPosEncoding.Available.Select(x => new CodePageOption(x.Index, x.Name)));
    }

    // ── Option lists ─────────────────────────────────────────────────────────────────────────

    public ObservableCollection<PrinterConnectionType> ConnectionTypes { get; }
    public ObservableCollection<PaperWidth> PaperWidths { get; }
    public ObservableCollection<DrawerConnectionType> DrawerTypes { get; }
    public ObservableCollection<int> BaudRates { get; }
    public ObservableCollection<CodePageOption> CodePages { get; }
    public ObservableCollection<string> PrinterQueues { get; } = new();
    public ObservableCollection<string> SerialPorts { get; } = new();

    // ── Printer ──────────────────────────────────────────────────────────────────────────────

    private HardwareConfig? _config;

    [ObservableProperty] private string _profileName = "Front counter";
    [ObservableProperty] private string _thermalPrinterName = string.Empty;
    [ObservableProperty] private PrinterConnectionType _connectionType = PrinterConnectionType.WindowsDriver;
    [ObservableProperty] private string _networkHost = string.Empty;
    [ObservableProperty] private string _networkPortText = "9100";
    [ObservableProperty] private string _serialPortName = "COM1";
    [ObservableProperty] private string _baudRateText = "9600";
    [ObservableProperty] private string _usbVendorProductId = string.Empty;
    [ObservableProperty] private PaperWidth _paperWidth = PaperWidth.Mm80;
    [ObservableProperty] private CodePageOption? _selectedCodePage;
    [ObservableProperty] private bool _autoCut = true;
    [ObservableProperty] private bool _printQrCode = true;
    [ObservableProperty] private bool _printReceiptBarcode = true;
    [ObservableProperty] private bool _printLogo = true;
    [ObservableProperty] private string _receiptCopiesText = "1";

    // ── Drawer ───────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _openDrawerCommand = "ESC p 0 25 250";
    [ObservableProperty] private bool _openDrawerOnSale = true;
    [ObservableProperty] private bool _openDrawerOnCashPayment = true;
    [ObservableProperty] private DrawerConnectionType _drawerConnectionType = DrawerConnectionType.PrinterKickPin2;

    // ── Scanner ──────────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _scannerPort = string.Empty;
    [ObservableProperty] private string _scannerBaudRateText = "9600";
    [ObservableProperty] private string _scannerMinLengthText = "3";
    [ObservableProperty] private string _scannerTimeoutText = "60";

    // ── Store identity ───────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _storeName = string.Empty;
    [ObservableProperty] private string _storeAddress = string.Empty;
    [ObservableProperty] private string _storePhone = string.Empty;
    [ObservableProperty] private string _taxRegistrationNumber = string.Empty;
    [ObservableProperty] private string _receiptFooter = string.Empty;
    [ObservableProperty] private string _digitalReceiptBaseUrl = string.Empty;

    // ── Test state ───────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _testResult = "No test run yet.";
    [ObservableProperty] private bool _lastTestPassed;
    [ObservableProperty] private string _drawerPulseBytes = string.Empty;

    public bool CanManage => _session.HasPermission(Permissions.ManageHardware);

    public string ColumnsPreview => PaperWidth == PaperWidth.Mm58 ? "32 columns" : "48 columns";

    partial void OnPaperWidthChanged(PaperWidth value) => OnPropertyChanged(nameof(ColumnsPreview));

    partial void OnOpenDrawerCommandChanged(string value) =>
        DrawerPulseBytes = DrawerCommandParser.Explain(value);

    public override async Task LoadAsync(CancellationToken ct = default)
    {
        await RunAsync(async token =>
        {
            _config = await _configs.GetActiveAsync(token).ConfigureAwait(true);
            var config = _config;

            ProfileName = config.ProfileName;
            ThermalPrinterName = config.ThermalPrinterName;
            ConnectionType = config.ConnectionType;
            NetworkHost = config.NetworkHost ?? string.Empty;
            NetworkPortText = config.NetworkPort.ToString();
            SerialPortName = config.SerialPortName ?? "COM1";
            BaudRateText = config.BaudRate.ToString();
            UsbVendorProductId = config.UsbVendorProductId ?? string.Empty;
            PaperWidth = config.PaperWidth;
            SelectedCodePage = CodePages.FirstOrDefault(c => c.Index == config.CodePage) ?? CodePages.FirstOrDefault();
            AutoCut = config.AutoCut;
            PrintQrCode = config.PrintQrCode;
            PrintReceiptBarcode = config.PrintReceiptBarcode;
            PrintLogo = config.PrintLogo;
            ReceiptCopiesText = config.ReceiptCopies.ToString();

            OpenDrawerCommand = config.OpenDrawerCommand;
            OpenDrawerOnSale = config.OpenDrawerOnSale;
            OpenDrawerOnCashPayment = config.OpenDrawerOnCashPayment;
            DrawerConnectionType = config.DrawerConnectionType;

            ScannerPort = config.BarcodeScannerPort ?? string.Empty;
            ScannerBaudRateText = config.BarcodeScannerBaudRate.ToString();
            ScannerMinLengthText = config.ScannerMinLength.ToString();
            ScannerTimeoutText = config.ScannerInterKeyTimeoutMs.ToString();

            StoreName = config.StoreName;
            StoreAddress = config.StoreAddress ?? string.Empty;
            StorePhone = config.StorePhone ?? string.Empty;
            TaxRegistrationNumber = config.TaxRegistrationNumber ?? string.Empty;
            ReceiptFooter = config.ReceiptFooter ?? string.Empty;
            DigitalReceiptBaseUrl = config.DigitalReceiptBaseUrl ?? string.Empty;

            DrawerPulseBytes = DrawerCommandParser.Explain(OpenDrawerCommand);
            RefreshDevices();
        }, "Could not load hardware settings.");
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        PrinterQueues.Clear();
        SerialPorts.Clear();

        try
        {
            foreach (var port in SerialPort.GetPortNames().OrderBy(p => p))
            {
                SerialPorts.Add(port);
            }
        }
        catch (Exception ex)
        {
            Notifier.Warn($"Cannot enumerate COM ports: {ex.Message}");
        }

        try
        {
            using var server = new System.Printing.LocalPrintServer();
            using var queues = server.GetPrintQueues(new[]
            {
                System.Printing.EnumeratedPrintQueueTypes.Local,
                System.Printing.EnumeratedPrintQueueTypes.Connections
            });

            foreach (var queue in queues.OrderBy(q => q.FullName))
            {
                PrinterQueues.Add(queue.FullName);
            }
        }
        catch (Exception ex)
        {
            Notifier.Warn($"Cannot enumerate printers: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanManage)
        {
            Notifier.Warn("Only an administrator can change hardware settings.");
            return;
        }

        if (_config is null)
        {
            return;
        }

        var config = BuildConfig();
        config.Id = _config.Id;
        config.IsActive = true;

        await RunAsync(async ct =>
        {
            await _configs.SaveAsync(config, ct).ConfigureAwait(true);
            _config = config;
            Notifier.Success("Hardware settings saved.");
        }, "Could not save hardware settings.");
    }

    private HardwareConfig BuildConfig()
    {
        var config = _config ?? new HardwareConfig();

        config.ProfileName = string.IsNullOrWhiteSpace(ProfileName) ? "Default" : ProfileName.Trim();
        config.ThermalPrinterName = ThermalPrinterName;
        config.ConnectionType = ConnectionType;
        config.NetworkHost = string.IsNullOrWhiteSpace(NetworkHost) ? null : NetworkHost.Trim();
        config.NetworkPort = ParseInt(NetworkPortText, 9100);
        config.SerialPortName = string.IsNullOrWhiteSpace(SerialPortName) ? null : SerialPortName;
        config.BaudRate = ParseInt(BaudRateText, 9600);
        config.UsbVendorProductId = string.IsNullOrWhiteSpace(UsbVendorProductId) ? null : UsbVendorProductId.Trim();
        config.PaperWidth = PaperWidth;
        config.CodePage = SelectedCodePage?.Index ?? (byte)16;
        config.AutoCut = AutoCut;
        config.PrintQrCode = PrintQrCode;
        config.PrintReceiptBarcode = PrintReceiptBarcode;
        config.PrintLogo = PrintLogo;
        config.ReceiptCopies = Math.Clamp(ParseInt(ReceiptCopiesText, 1), 1, 3);

        config.OpenDrawerCommand = string.IsNullOrWhiteSpace(OpenDrawerCommand) ? "ESC p 0 25 250" : OpenDrawerCommand.Trim();
        config.OpenDrawerOnSale = OpenDrawerOnSale;
        config.OpenDrawerOnCashPayment = OpenDrawerOnCashPayment;
        config.DrawerConnectionType = DrawerConnectionType;

        config.BarcodeScannerPort = string.IsNullOrWhiteSpace(ScannerPort) ? null : ScannerPort;
        config.BarcodeScannerBaudRate = ParseInt(ScannerBaudRateText, 9600);
        config.ScannerMinLength = Math.Clamp(ParseInt(ScannerMinLengthText, 3), 1, 20);
        config.ScannerInterKeyTimeoutMs = Math.Clamp(ParseInt(ScannerTimeoutText, 60), 20, 200);

        config.StoreName = string.IsNullOrWhiteSpace(StoreName) ? "Kacher Market" : StoreName.Trim();
        config.StoreAddress = StoreAddress;
        config.StorePhone = StorePhone;
        config.TaxRegistrationNumber = TaxRegistrationNumber;
        config.ReceiptFooter = ReceiptFooter;
        config.DigitalReceiptBaseUrl = DigitalReceiptBaseUrl;

        return config;
    }

    [RelayCommand]
    private async Task TestPrintAsync()
    {
        var config = BuildConfig();

        IsTesting = true;
        TestResult = "Sending test receipt…";

        // Detect the most common commissioning mistake before wasting paper: a queue name that
        // does not exist on this machine. The print is still attempted so the log shows the real error.
        if (config.ConnectionType == PrinterConnectionType.WindowsDriver
            && !_transports.PrinterQueueExists(config.ThermalPrinterName))
        {
            TestResult = $"Queue '{config.ThermalPrinterName}' was not found — attempting anyway…";
        }

        try
        {
            var result = await _printer.PrintTestAsync(config).ConfigureAwait(true);
            LastTestPassed = result.Success;
            TestResult = result.Success
                ? $"Test receipt sent in {result.Duration.TotalMilliseconds:0} ms ({result.Bytes} bytes) via {config.ConnectionType}."
                : $"Failed: {result.Message}";

            if (result.Success)
            {
                Notifier.Success("Test receipt sent.");
            }
            else
            {
                Notifier.Error(result.Message);
            }
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task TestDrawerAsync()
    {
        var config = BuildConfig();

        IsTesting = true;
        TestResult = "Pulsing drawer kick pin…";

        try
        {
            var result = await _printer.OpenDrawerAsync(config).ConfigureAwait(true);
            LastTestPassed = result.Success;
            TestResult = result.Success
                ? $"Drawer pulse sent ({DrawerCommandParser.Explain(config.OpenDrawerCommand)})."
                : $"Drawer failed: {result.Message}";

            if (result.Success)
            {
                Notifier.Success("Drawer pulse sent.");
            }
            else
            {
                Notifier.Error(result.Message);
            }
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task ProbePrinterAsync()
    {
        var config = BuildConfig();
        IsTesting = true;

        try
        {
            var probe = await _printer.ProbeAsync(config).ConfigureAwait(true);
            LastTestPassed = probe.IsAvailable;
            TestResult = $"{config.ConnectionType}: {probe.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void UseDefaultDrawerPulse()
    {
        OpenDrawerCommand = "ESC p 0 25 250";
        DrawerConnectionType = DrawerConnectionType.PrinterKickPin2;
    }

    [RelayCommand]
    private void UsePin5Pulse()
    {
        OpenDrawerCommand = "ESC p 1 25 250";
        DrawerConnectionType = DrawerConnectionType.PrinterKickPin5;
    }

    [RelayCommand]
    private void SelectDetectedQueue()
    {
        if (PrinterQueues.Count > 0 && string.IsNullOrWhiteSpace(ThermalPrinterName))
        {
            ThermalPrinterName = PrinterQueues[0];
        }
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;
}
