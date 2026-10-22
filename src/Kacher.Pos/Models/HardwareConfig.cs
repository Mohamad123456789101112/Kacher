using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Kacher.Pos.Models;

/// <summary>
/// Persisted hardware profile. One active row is used at a time (<see cref="IsActive"/>);
/// keeping history lets a store switch between a 58 mm kiosk printer and an 80 mm till printer.
/// </summary>
public class HardwareConfig
{
    public int Id { get; set; }

    [MaxLength(128)]
    public string ProfileName { get; set; } = "Default";

    public bool IsActive { get; set; } = true;

    // ── Receipt printer ────────────────────────────────────────────────────────────────────────
    /// <summary>Windows spooler queue name, e.g. "EPSON TM-T20III Receipt".</summary>
    [MaxLength(256)]
    public string ThermalPrinterName { get; set; } = string.Empty;

    public PrinterConnectionType ConnectionType { get; set; } = PrinterConnectionType.WindowsDriver;

    /// <summary>Host name or IP for <see cref="PrinterConnectionType.Network"/>.</summary>
    [MaxLength(128)]
    public string? NetworkHost { get; set; }

    /// <summary>Raw socket port; 9100 is the ESC/POS de-facto standard.</summary>
    public int NetworkPort { get; set; } = 9100;

    /// <summary>COM port for serial printers, e.g. "COM3".</summary>
    [MaxLength(16)]
    public string? SerialPortName { get; set; }

    public int BaudRate { get; set; } = 9600;

    /// <summary>USB printer VID/PID match string, e.g. "04B8:0E15" (Epson TM-T20III).</summary>
    [MaxLength(32)]
    public string? UsbVendorProductId { get; set; }

    public PaperWidth PaperWidth { get; set; } = PaperWidth.Mm80;

    /// <summary>ESC/POS code page index (0 = CP437, 16 = WPC1252, 22 = CP864, 25 = WPC1256).</summary>
    public byte CodePage { get; set; } = 16;

    /// <summary>Enable the "cut paper" command at the end of each receipt.</summary>
    public bool AutoCut { get; set; } = true;

    /// <summary>Print a QR code linking to the digital receipt endpoint.</summary>
    public bool PrintQrCode { get; set; } = true;

    public bool PrintReceiptBarcode { get; set; } = true;

    public int ReceiptCopies { get; set; } = 1;

    /// <summary>Print the store logo (Assets/kacher.png or a configured file) above the header.</summary>
    public bool PrintLogo { get; set; } = true;

    // ── Cash drawer ────────────────────────────────────────────────────────────────────────────
    /// <summary>Verbatim ESC/POS payload. Default pulses drawer pin 2: ESC p 0 25 250.</summary>
    [MaxLength(64)]
    public string OpenDrawerCommand { get; set; } = "ESC p 0 25 250";

    public bool OpenDrawerOnSale { get; set; } = true;

    public bool OpenDrawerOnCashPayment { get; set; } = true;

    public DrawerConnectionType DrawerConnectionType { get; set; } = DrawerConnectionType.PrinterKickPin2;

    // ── Barcode scanner ────────────────────────────────────────────────────────────────────────
    /// <summary>COM port for a serial wedge scanner; blank means "keyboard emulation".</summary>
    [MaxLength(16)]
    public string? BarcodeScannerPort { get; set; }

    public int BarcodeScannerBaudRate { get; set; } = 9600;

    /// <summary>Milliseconds of silence that terminates a keyboard-wedge scan burst.</summary>
    public int ScannerInterKeyTimeoutMs { get; set; } = 60;

    /// <summary>Minimum barcode length accepted from the keyboard wedge.</summary>
    public int ScannerMinLength { get; set; } = 3;

    public bool ScannerBeepOnScan { get; set; } = true;

    // ── Store identity printed on receipts ─────────────────────────────────────────────────────
    [MaxLength(128)]
    public string StoreName { get; set; } = "Kacher Market";

    [MaxLength(256)]
    public string? StoreAddress { get; set; } = "12 Corniche Road, Alexandria";

    [MaxLength(64)]
    public string? StorePhone { get; set; } = "+20 3 555 0100";

    [MaxLength(64)]
    public string? TaxRegistrationNumber { get; set; }

    [MaxLength(256)]
    public string? ReceiptFooter { get; set; } = "Thank you for shopping with us!";

    /// <summary>Base URL embedded in the receipt QR code.</summary>
    [MaxLength(256)]
    public string? DigitalReceiptBaseUrl { get; set; } = "https://receipts.kacher.local/i/";

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Columns available at Font A for the configured roll width.</summary>
    [NotMapped]
    public int ReceiptColumns => PaperWidth == PaperWidth.Mm58 ? 32 : 48;
}

/// <summary>Key/value store for anything that does not deserve its own table (theme, locale…).</summary>
public class AppSetting
{
    [MaxLength(128)]
    public string Key { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? Value { get; set; }
}

/// <summary>Append-only audit trail for security-sensitive actions (price edits, refunds, drawer opens).</summary>
public class AuditLog
{
    public long Id { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public int? UserId { get; set; }

    [MaxLength(64)]
    public string? UserName { get; set; }

    [MaxLength(64)]
    public string Action { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? Detail { get; set; }
}
