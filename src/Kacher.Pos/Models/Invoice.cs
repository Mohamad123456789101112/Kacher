using System.ComponentModel.DataAnnotations;

namespace Kacher.Pos.Models;

/// <summary>
/// Immutable record of a completed sale. Money columns are <see cref="decimal"/> mapped to TEXT
/// by EF Core's SQLite provider, which avoids binary floating point drift in totals.
/// </summary>
public class Invoice
{
    public int Id { get; set; }

    /// <summary>Human readable, gap-free sequence e.g. "INV-20261022-0007". Unique index.</summary>
    [MaxLength(32)]
    public string InvoiceNumber { get; set; } = string.Empty;

    /// <summary>
    /// Always UTC. UI, receipts and reports convert with <c>ToLocalTime()</c> at the edge; the
    /// invoice number's date segment is derived from the local time of sale.
    /// </summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Line subtotal before invoice-level discount and tax.</summary>
    public decimal Subtotal { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    /// <summary>Cash tendered by the customer (used to compute change).</summary>
    public decimal AmountTendered { get; set; }

    public decimal ChangeGiven { get; set; }

    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Completed;

    public int? CashierId { get; set; }

    public User? Cashier { get; set; }

    public int? ShiftId { get; set; }

    public Shift? Shift { get; set; }

    /// <summary>VAT breakdown lines serialised as JSON at time of sale (audit-safe snapshot).</summary>
    public string? TaxBreakdownJson { get; set; }

    /// <summary>Set when a refund is processed so the original sale can be traced.</summary>
    public int? RefundedInvoiceId { get; set; }

    [MaxLength(256)]
    public string? Note { get; set; }

    public ICollection<InvoiceItem> Items { get; set; } = new List<InvoiceItem>();

    public ICollection<InvoicePayment> Payments { get; set; } = new List<InvoicePayment>();
}

/// <summary>One product line on an invoice. Prices are snapshotted, not referenced live.</summary>
public class InvoiceItem
{
    public int Id { get; set; }

    public int InvoiceId { get; set; }

    public Invoice? Invoice { get; set; }

    public int? ProductId { get; set; }

    public Product? Product { get; set; }

    /// <summary>Denormalised name so historical receipts survive catalogue renames.</summary>
    [MaxLength(160)]
    public string ProductName { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Barcode { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    /// <summary>Line-level discount in currency units.</summary>
    public decimal DiscountAmount { get; set; }

    /// <summary>Net line price after discount (excludes tax).</summary>
    public decimal TotalPrice { get; set; }

    public decimal TaxRate { get; set; }

    public decimal TaxAmount { get; set; }
}

/// <summary>Tender row — supports split payments (part cash, part card).</summary>
public class InvoicePayment
{
    public int Id { get; set; }

    public int InvoiceId { get; set; }

    public Invoice? Invoice { get; set; }

    public PaymentMethod Method { get; set; }

    public decimal Amount { get; set; }

    [MaxLength(64)]
    public string? Reference { get; set; }
}
