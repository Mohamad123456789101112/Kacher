using System.ComponentModel.DataAnnotations;

namespace Kacher.Pos.Models;

/// <summary>Role assigned to an operator. Admin may re-open shifts, edit prices and change hardware.</summary>
public enum UserRole
{
    Cashier = 0,
    Supervisor = 1,
    Admin = 2
}

/// <summary>Settlement method recorded against an invoice.</summary>
public enum PaymentMethod
{
    Cash = 0,
    Card = 1,
    Split = 2
}

/// <summary>Lifecycle of a sale. Refunded invoices are kept for audit but excluded from net sales.</summary>
public enum InvoiceStatus
{
    Completed = 0,
    Refunded = 1,
    Voided = 2
}

/// <summary>Physical transport used by the receipt printer.</summary>
public enum PrinterConnectionType
{
    /// <summary>Windows spooler queue (works with virtually every installed driver).</summary>
    WindowsDriver = 0,
    /// <summary>USB-attached printer addressed through its spooler queue in RAW mode.</summary>
    Usb = 1,
    /// <summary>TCP 9100 "JetDirect" raw socket.</summary>
    Network = 2,
    /// <summary>RS-232 / virtual COM port.</summary>
    Serial = 3
}

/// <summary>Paper roll width, drives the character column layout of the receipt template.</summary>
public enum PaperWidth
{
    /// <summary>58 mm roll — 32 characters at Font A.</summary>
    Mm58 = 58,
    /// <summary>80 mm roll — 48 characters at Font A.</summary>
    Mm80 = 80
}

/// <summary>Cash drawer wiring. Most drawers are driven by the printer's RJ-11 kick port.</summary>
public enum DrawerConnectionType
{
    /// <summary>Pulse pin 2 via the ESC/POS printer.</summary>
    PrinterKickPin2 = 0,
    /// <summary>Pulse pin 5 via the ESC/POS printer.</summary>
    PrinterKickPin5 = 1,
    /// <summary>Standalone drawer driven by its own COM port (rare).</summary>
    SerialPort = 2
}

/// <summary>Inventory movement reason, used for the stock ledger.</summary>
public enum StockMovementReason
{
    Sale = 0,
    Refund = 1,
    ManualAdjustment = 2,
    GoodsReceived = 3,
    StockTake = 4,
    Wastage = 5
}

/// <summary>Lifecycle of an operator shift (cash drawer session).</summary>
public enum ShiftStatus
{
    Open = 0,
    Closed = 1
}

/// <summary>Severity used by the top status bar hardware pills.</summary>
public enum HardwareHealth
{
    Unknown = 0,
    Online = 1,
    Degraded = 2,
    Offline = 3
}
