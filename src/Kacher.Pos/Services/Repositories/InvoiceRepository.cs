using Kacher.Pos.Data;
using Kacher.Pos.Models;
using Microsoft.EntityFrameworkCore;

namespace Kacher.Pos.Services.Repositories;

public interface IInvoiceRepository
{
    /// <summary>
    /// Persists an invoice, its lines, its tenders and the stock deduction in a single SQLite
    /// transaction. Returns the invoice with a gap-free <see cref="Invoice.InvoiceNumber"/> assigned.
    /// </summary>
    Task<Invoice> CreateAsync(Invoice invoice, bool deductStock, CancellationToken ct = default);

    Task<Invoice?> GetByIdAsync(int id, CancellationToken ct = default);

    Task<IReadOnlyList<Invoice>> GetByShiftAsync(int shiftId, CancellationToken ct = default);

    Task<IReadOnlyList<InvoiceRow>> GetRecentAsync(int take = 100, DateTime? from = null, CancellationToken ct = default);

    Task<Invoice> RefundAsync(int invoiceId, int userId, string reason, CancellationToken ct = default);

    Task<string> PeekNextNumberAsync(CancellationToken ct = default);
}

public sealed class InvoiceRepository : IInvoiceRepository
{
    private readonly IDbContextFactory<KacherDbContext> _factory;

    public InvoiceRepository(IDbContextFactory<KacherDbContext> factory) => _factory = factory;

    private static string BuildPrefix(DateTime when) => $"INV-{when:yyyyMMdd}-";

    public async Task<string> PeekNextNumberAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await NextNumberAsync(db, DateTime.Now, ct).ConfigureAwait(false);
    }

    private static async Task<string> NextNumberAsync(KacherDbContext db, DateTime when, CancellationToken ct)
    {
        var prefix = BuildPrefix(when);

        // Ordered by the string column: the "D4" sequence keeps lexical and numeric order aligned.
        var last = await db.Invoices
            .AsNoTracking()
            .Where(i => i.InvoiceNumber.StartsWith(prefix))
            .OrderByDescending(i => i.InvoiceNumber)
            .Select(i => i.InvoiceNumber)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var next = 1;
        if (last is not null && int.TryParse(last[prefix.Length..], out var parsed))
        {
            next = parsed + 1;
        }

        return prefix + next.ToString("D4");
    }

    public async Task<Invoice> CreateAsync(Invoice invoice, bool deductStock, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // IMMEDIATE transaction: takes the write lock up front so two clicks of "Process" can never
        // produce the same invoice number (SQLite would otherwise defer the lock and deadlock-retry).
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        invoice.InvoiceNumber = await NextNumberAsync(db, invoice.CreatedAtUtc.ToLocalTime(), ct).ConfigureAwait(false);

        db.Invoices.Add(invoice);

        if (deductStock)
        {
            foreach (var item in invoice.Items.Where(i => i.ProductId is not null))
            {
                var product = await db.Products
                    .FirstOrDefaultAsync(p => p.Id == item.ProductId, ct)
                    .ConfigureAwait(false);

                if (product is null)
                {
                    continue;
                }

                product.StockQuantity -= item.Quantity;
                product.UpdatedAtUtc = DateTime.UtcNow;

                // Weighted items may legitimately go negative when a night-shift stock count is late;
                // everything else is clamped at zero and flagged in the stock ledger note.
                if (!product.IsWeighted && product.StockQuantity < 0)
                {
                    product.StockQuantity = 0;
                }

                db.StockMovements.Add(new StockMovement
                {
                    ProductId = product.Id,
                    QuantityDelta = -item.Quantity,
                    QuantityAfter = product.StockQuantity,
                    Reason = StockMovementReason.Sale,
                    Note = invoice.InvoiceNumber,
                    UserId = invoice.CashierId,
                    CreatedAtUtc = invoice.CreatedAtUtc
                });
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        return invoice;
    }

    public async Task<Invoice?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Invoices
            .AsNoTracking()
            .Include(i => i.Items)
            .Include(i => i.Payments)
            .Include(i => i.Cashier)
            .Include(i => i.Shift)
            .FirstOrDefaultAsync(i => i.Id == id, ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Invoice>> GetByShiftAsync(int shiftId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Invoices
            .AsNoTracking()
            .Include(i => i.Items)
            .Include(i => i.Payments)
            .Where(i => i.ShiftId == shiftId)
            .OrderByDescending(i => i.CreatedAtUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InvoiceRow>> GetRecentAsync(int take = 100, DateTime? from = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = db.Invoices.AsNoTracking();

        if (from is { } start)
        {
            query = query.Where(i => i.CreatedAtUtc >= start);
        }

        // Server-side projection keeps the payload tiny — no need to materialise line entities.
        return await query
            .OrderByDescending(i => i.CreatedAtUtc)
            .Take(take)
            .Select(i => new InvoiceRow(
                i.Id,
                i.InvoiceNumber,
                i.CreatedAtUtc,
                i.TotalAmount,
                i.PaymentMethod,
                i.Status,
                i.Items.Count,
                i.Cashier != null ? i.Cashier.DisplayName : null))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<Invoice> RefundAsync(int invoiceId, int userId, string reason, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var original = await db.Invoices
            .Include(i => i.Items)
            .Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Invoice not found.");

        if (original.Status == InvoiceStatus.Refunded)
        {
            throw new InvalidOperationException("Invoice has already been refunded.");
        }

        original.Status = InvoiceStatus.Refunded;

        // Return stock to the shelf.
        foreach (var item in original.Items.Where(i => i.ProductId is not null))
        {
            var product = await db.Products.FirstOrDefaultAsync(p => p.Id == item.ProductId, ct).ConfigureAwait(false);
            if (product is null)
            {
                continue;
            }

            product.StockQuantity += item.Quantity;
            db.StockMovements.Add(new StockMovement
            {
                ProductId = product.Id,
                QuantityDelta = item.Quantity,
                QuantityAfter = product.StockQuantity,
                Reason = StockMovementReason.Refund,
                Note = $"Refund of {original.InvoiceNumber}: {reason}",
                UserId = userId,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        // A refund is a first-class negative document so the Z-report stays auditable.
        var refund = new Invoice
        {
            InvoiceNumber = $"{original.InvoiceNumber}-R",
            CreatedAtUtc = DateTime.UtcNow,
            Subtotal = -original.Subtotal,
            DiscountAmount = -original.DiscountAmount,
            TaxAmount = -original.TaxAmount,
            TotalAmount = -original.TotalAmount,
            AmountTendered = -original.TotalAmount,
            PaymentMethod = original.PaymentMethod,
            Status = InvoiceStatus.Completed,
            CashierId = userId,
            ShiftId = original.ShiftId,
            RefundedInvoiceId = original.Id,
            Note = reason,
            TaxBreakdownJson = original.TaxBreakdownJson
        };

        foreach (var item in original.Items)
        {
            refund.Items.Add(new InvoiceItem
            {
                ProductId = item.ProductId,
                ProductName = item.ProductName,
                Barcode = item.Barcode,
                Quantity = -item.Quantity,
                UnitPrice = item.UnitPrice,
                DiscountAmount = -item.DiscountAmount,
                TotalPrice = -item.TotalPrice,
                TaxRate = item.TaxRate,
                TaxAmount = -item.TaxAmount
            });
        }

        foreach (var payment in original.Payments)
        {
            refund.Payments.Add(new InvoicePayment
            {
                Method = payment.Method,
                Amount = -payment.Amount,
                Reference = $"REFUND {original.InvoiceNumber}"
            });
        }

        db.Invoices.Add(refund);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        return refund;
    }
}
