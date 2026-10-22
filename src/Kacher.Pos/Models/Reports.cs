namespace Kacher.Pos.Models;

/// <summary>Aggregated figures for one shift, used by the closing screen and the Z-report.</summary>
public sealed class ShiftSummary
{
    public int ShiftId { get; init; }
    public string ShiftNumber { get; init; } = string.Empty;
    public string CashierName { get; init; } = string.Empty;
    public DateTime OpenedAt { get; init; }
    public DateTime ClosedAt { get; init; }

    public int InvoiceCount { get; init; }
    public int ItemCount { get; init; }

    public decimal GrossSales { get; init; }
    public decimal NetSales { get; init; }
    public decimal TaxTotal { get; init; }
    public decimal DiscountTotal { get; init; }
    public decimal RefundTotal { get; init; }

    public decimal CashTotal { get; init; }
    public decimal CardTotal { get; init; }
    public decimal SplitTotal { get; init; }

    public decimal OpeningFloat { get; init; }
    public decimal PaidIn { get; init; }
    public decimal PaidOut { get; init; }
    public decimal CountedCash { get; init; }
    public decimal CashDifference { get; init; }

    /// <summary>Expected drawer contents: float + cash sales + paid-in − paid-out.</summary>
    public decimal ExpectedCash => OpeningFloat + CashTotal + PaidIn - PaidOut;

    public decimal AverageBasket => InvoiceCount == 0 ? 0m : Math.Round(GrossSales / InvoiceCount, 2);

    /// <summary>Tax split per rate, e.g. { 0.14 → 312.40, 0.00 → 0.00 }.</summary>
    public IReadOnlyList<TaxBand> TaxBands { get; init; } = Array.Empty<TaxBand>();
}

public sealed record TaxBand(decimal Rate, decimal NetAmount, decimal TaxAmount)
{
    public decimal GrossAmount => NetAmount + TaxAmount;
    public string Label => $"{Rate * 100:0.##}%";
}

/// <summary>A day's trade, used by ReportsView and the exported PDF summary.</summary>
public sealed class DailyReport
{
    public DateOnly Date { get; init; }
    public int InvoiceCount { get; init; }
    public decimal GrossSales { get; init; }
    public decimal NetSales { get; init; }
    public decimal TaxTotal { get; init; }
    public decimal DiscountTotal { get; init; }
    public decimal CashTotal { get; init; }
    public decimal CardTotal { get; init; }
    public decimal RefundTotal { get; init; }
    public decimal AverageBasket => InvoiceCount == 0 ? 0m : Math.Round(GrossSales / InvoiceCount, 2);
    public IReadOnlyList<TaxBand> TaxBands { get; init; } = Array.Empty<TaxBand>();
    public IReadOnlyList<HourlySales> Hourly { get; init; } = Array.Empty<HourlySales>();
    public IReadOnlyList<TopProduct> TopProducts { get; init; } = Array.Empty<TopProduct>();
}

public sealed record HourlySales(int Hour, int InvoiceCount, decimal Revenue);

public sealed record TopProduct(int ProductId, string Name, decimal Quantity, decimal Revenue);

/// <summary>One row on the invoice history list.</summary>
public sealed record InvoiceRow(
    int Id,
    string InvoiceNumber,
    DateTime CreatedAt,
    decimal TotalAmount,
    PaymentMethod PaymentMethod,
    InvoiceStatus Status,
    int ItemCount,
    string? CashierName);

/// <summary>Result of a completed sale, handed to the UI and the printing service.</summary>
public sealed record SaleResult(Invoice Invoice, ShiftSummary? ShiftSnapshot);
