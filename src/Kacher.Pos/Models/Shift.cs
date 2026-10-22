using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Kacher.Pos.Models;

/// <summary>
/// A cash-drawer session between "Open Shift" and "End of Day". All invoices carry a ShiftId so
/// the closing report can be produced without scanning timestamps.
/// </summary>
public class Shift
{
    public int Id { get; set; }

    [MaxLength(32)]
    public string ShiftNumber { get; set; } = string.Empty;

    public int? UserId { get; set; }

    public User? User { get; set; }

    public DateTime OpenedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ClosedAtUtc { get; set; }

    public decimal OpeningFloat { get; set; }

    /// <summary>Cash physically counted in the drawer at close.</summary>
    public decimal? CountedCash { get; set; }

    /// <summary>Counted - expected. Positive means an overage.</summary>
    public decimal? CashDifference { get; set; }

    public ShiftStatus Status { get; set; } = ShiftStatus.Open;

    [MaxLength(512)]
    public string? ClosingNote { get; set; }

    /// <summary>Serialised Z-report snapshot so reprinted reports always match the original.</summary>
    public string? ZReportJson { get; set; }

    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();

    public ICollection<CashMovement> CashMovements { get; set; } = new List<CashMovement>();

    [NotMapped]
    public decimal CashSales => Invoices
        .Where(i => i.Status == InvoiceStatus.Completed)
        .SelectMany(i => i.Payments)
        .Where(p => p.Method == PaymentMethod.Cash)
        .Sum(p => p.Amount);

    [NotMapped]
    public decimal ExpectedCash => OpeningFloat
        + CashSales
        + CashMovements.Where(m => m.Kind == CashMovementKind.PaidIn).Sum(m => m.Amount)
        - CashMovements.Where(m => m.Kind == CashMovementKind.PaidOut).Sum(m => m.Amount);
}

/// <summary>Cash in/out during a shift (petty cash, bank drops, float top-ups).</summary>
public class CashMovement
{
    public int Id { get; set; }

    public int ShiftId { get; set; }

    public Shift? Shift { get; set; }

    public CashMovementKind Kind { get; set; }

    public decimal Amount { get; set; }

    [MaxLength(256)]
    public string? Reason { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public enum CashMovementKind
{
    PaidIn = 0,
    PaidOut = 1,
    BankDrop = 2,
    FloatAdjustment = 3
}
