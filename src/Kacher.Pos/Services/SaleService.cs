using System.Text.Json;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Kacher.Pos.Services.Repositories;

namespace Kacher.Pos.Services;

// ────────────────────────────────────────────────────────────────────────────────────────────────
// Request DTOs. The ViewModel converts its observable cart into these plain records so the money
// math runs on immutable data and can be unit tested without a Dispatcher.
// ────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record SaleLineRequest(
    int? ProductId,
    string ProductName,
    string Barcode,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal TaxRate);

public sealed record PaymentRequest(PaymentMethod Method, decimal Amount, string? Reference = null);

public sealed record SaleRequest(
    int CashierId,
    int? ShiftId,
    IReadOnlyList<SaleLineRequest> Lines,
    decimal InvoiceDiscount,
    IReadOnlyList<PaymentRequest> Payments,
    string? Note = null);

/// <summary>Pre-payment arithmetic preview shown in the bottom summary bar.</summary>
public sealed record SaleTotals(
    decimal Subtotal,
    decimal LineDiscount,
    decimal InvoiceDiscount,
    decimal NetSales,
    decimal TaxAmount,
    decimal Total)
{
    public static SaleTotals Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

public interface ISaleService
{
    /// <summary>Pure calculation — no I/O, safe to call on every keystroke.</summary>
    SaleTotals Calculate(IReadOnlyList<SaleLineRequest> lines, decimal invoiceDiscount);

    Task<Invoice> CommitAsync(SaleRequest request, CancellationToken ct = default);
}

public sealed class SaleService : ISaleService
{
    private readonly IInvoiceRepository _invoices;
    private readonly IClock _clock;
    private readonly IAppLog _log;

    public SaleService(IInvoiceRepository invoices, IClock clock, IAppLog log)
    {
        _invoices = invoices;
        _clock = clock;
        _log = log;
    }

    public SaleTotals Calculate(IReadOnlyList<SaleLineRequest> lines, decimal invoiceDiscount)
    {
        if (lines.Count == 0)
        {
            return SaleTotals.Empty;
        }

        // 1. Line level maths (quantity × price − item discount).
        var gross = lines.Sum(l => l.Quantity * l.UnitPrice);
        var lineDiscount = lines.Sum(l => Math.Min(l.DiscountAmount, l.Quantity * l.UnitPrice));
        var netBeforeInvoiceDiscount = gross - lineDiscount;

        // 2. Spread the invoice-level discount proportionally so each line stays tax-correct.
        var discount = Math.Clamp(invoiceDiscount, 0m, netBeforeInvoiceDiscount);

        var net = 0m;
        var tax = 0m;

        foreach (var line in lines)
        {
            var lineNet = Math.Max(0m, (line.Quantity * line.UnitPrice) - line.DiscountAmount);
            var share = netBeforeInvoiceDiscount == 0m ? 0m : lineNet / netBeforeInvoiceDiscount;
            var netAfterInvoiceDiscount = Math.Round(lineNet - (discount * share), 2, MidpointRounding.AwayFromZero);

            net += netAfterInvoiceDiscount;
            tax += Math.Round(netAfterInvoiceDiscount * line.TaxRate, 2, MidpointRounding.AwayFromZero);
        }

        net = Math.Round(net, 2, MidpointRounding.AwayFromZero);
        tax = Math.Round(tax, 2, MidpointRounding.AwayFromZero);

        return new SaleTotals(
            Subtotal: Math.Round(gross, 2, MidpointRounding.AwayFromZero),
            LineDiscount: Math.Round(lineDiscount, 2, MidpointRounding.AwayFromZero),
            InvoiceDiscount: Math.Round(discount, 2, MidpointRounding.AwayFromZero),
            NetSales: net,
            TaxAmount: tax,
            Total: Math.Round(net + tax, 2, MidpointRounding.AwayFromZero));
    }

    public async Task<Invoice> CommitAsync(SaleRequest request, CancellationToken ct = default)
    {
        if (request.Lines.Count == 0)
        {
            throw new InvalidOperationException("Cannot commit an empty sale.");
        }

        if (request.Payments.Count == 0)
        {
            throw new InvalidOperationException("A payment method is required.");
        }

        var totals = Calculate(request.Lines, request.InvoiceDiscount);

        // Half-open guard: floating point is not involved (decimal), so an epsilon is safe here and
        // prevents "1 cent short" rejections caused by split-tender rounding by the card terminal.
        var tendered = request.Payments.Sum(p => p.Amount);
        if (tendered + 0.005m < totals.Total)
        {
            throw new InvalidOperationException(
                $"Tendered {tendered:0.00} is less than the amount due {totals.Total:0.00}.");
        }

        var cashTendered = request.Payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);

        var invoice = new Invoice
        {
            CreatedAtUtc = _clock.UtcNow,
            CashierId = request.CashierId,
            ShiftId = request.ShiftId,
            Subtotal = totals.NetSales,
            DiscountAmount = totals.LineDiscount + totals.InvoiceDiscount,
            TaxAmount = totals.TaxAmount,
            TotalAmount = totals.Total,
            AmountTendered = Math.Round(tendered, 2, MidpointRounding.AwayFromZero),
            ChangeGiven = Math.Round(Math.Max(0m, cashTendered - totals.Total), 2, MidpointRounding.AwayFromZero),
            PaymentMethod = ResolveMethod(request.Payments),
            Status = InvoiceStatus.Completed,
            Note = request.Note,
            TaxBreakdownJson = JsonSerializer.Serialize(totals)
        };

        foreach (var line in request.Lines)
        {
            var lineNet = Math.Max(0m, (line.Quantity * line.UnitPrice) - line.DiscountAmount);
            invoice.Items.Add(new InvoiceItem
            {
                ProductId = line.ProductId,
                ProductName = line.ProductName,
                Barcode = line.Barcode,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                DiscountAmount = line.DiscountAmount,
                TotalPrice = Math.Round(lineNet, 2, MidpointRounding.AwayFromZero),
                TaxRate = line.TaxRate,
                TaxAmount = Math.Round(lineNet * line.TaxRate, 2, MidpointRounding.AwayFromZero)
            });
        }

        foreach (var payment in request.Payments)
        {
            invoice.Payments.Add(new InvoicePayment
            {
                Method = payment.Method,
                Amount = payment.Amount,
                Reference = payment.Reference
            });
        }

        var committed = await _invoices.CreateAsync(invoice, deductStock: true, ct).ConfigureAwait(false);
        _log.Info($"Invoice {committed.InvoiceNumber} committed for {committed.TotalAmount:0.00} " +
                  $"({committed.PaymentMethod}).");

        return committed;
    }

    private static PaymentMethod ResolveMethod(IReadOnlyList<PaymentRequest> payments) =>
        payments.Count == 1
            ? payments[0].Method
            : payments.Select(p => p.Method).Distinct().Count() == 1
                ? payments[0].Method
                : PaymentMethod.Split;
}
