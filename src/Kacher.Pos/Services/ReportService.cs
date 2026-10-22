using System.Globalization;
using System.Text;
using Kacher.Pos.Data;
using Kacher.Pos.Models;
using Microsoft.EntityFrameworkCore;

namespace Kacher.Pos.Services;

public interface IReportService
{
    Task<DailyReport> GetDailyReportAsync(DateOnly date, CancellationToken ct = default);

    Task<IReadOnlyList<DailyReport>> GetRangeAsync(DateOnly from, DateOnly to, CancellationToken ct = default);

    Task<IReadOnlyList<InvoiceRow>> GetInvoiceHistoryAsync(DateOnly from, DateOnly to, CancellationToken ct = default);

    Task<string> ExportRangeCsvAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}

public sealed class ReportService : IReportService
{
    private readonly IDbContextFactory<KacherDbContext> _factory;

    public ReportService(IDbContextFactory<KacherDbContext> factory) => _factory = factory;

    public async Task<DailyReport> GetDailyReportAsync(DateOnly date, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Half-open interval [start, end) — index-friendly on IX_Invoices_CreatedAt and immune to
        // the "23:59:59.999" rounding trap that plagues BETWEEN queries.
        // Dates arrive as local business days; timestamps are stored in UTC, so the boundary is
        // converted once here rather than calling ToLocalTime() on every row.
        var start = date.ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var end = date.AddDays(1).ToDateTime(TimeOnly.MinValue).ToUniversalTime();

        var invoices = await db.Invoices
            .AsNoTracking()
            .Include(i => i.Items)
            .Include(i => i.Payments)
            .Where(i => i.CreatedAtUtc >= start && i.CreatedAtUtc < end && i.Status != InvoiceStatus.Voided)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var completed = invoices.Where(i => i.RefundedInvoiceId == null).ToList();
        var refunds = invoices.Where(i => i.RefundedInvoiceId != null).ToList();

        var taxBands = completed
            .SelectMany(i => i.Items)
            .GroupBy(i => i.TaxRate)
            .Select(g => new TaxBand(
                g.Key,
                Math.Round(g.Sum(x => x.TotalPrice), 2),
                Math.Round(g.Sum(x => x.TaxAmount), 2)))
            .OrderByDescending(b => b.Rate)
            .ToList();

        var hourly = completed
            .GroupBy(i => i.CreatedAtUtc.ToLocalTime().Hour)
            .Select(g => new HourlySales(g.Key, g.Count(), Math.Round(g.Sum(i => i.TotalAmount), 2)))
            .OrderBy(h => h.Hour)
            .ToList();

        var topProducts = completed
            .SelectMany(i => i.Items)
            .GroupBy(i => new { i.ProductId, i.ProductName })
            .Select(g => new TopProduct(
                g.Key.ProductId ?? 0,
                g.Key.ProductName,
                Math.Round(g.Sum(x => x.Quantity), 2),
                Math.Round(g.Sum(x => x.TotalPrice), 2)))
            .OrderByDescending(p => p.Revenue)
            .Take(10)
            .ToList();

        return new DailyReport
        {
            Date = date,
            InvoiceCount = completed.Count,
            GrossSales = Math.Round(completed.Sum(i => i.TotalAmount), 2),
            NetSales = Math.Round(completed.Sum(i => i.Subtotal), 2),
            TaxTotal = Math.Round(completed.Sum(i => i.TaxAmount), 2),
            DiscountTotal = Math.Round(completed.Sum(i => i.DiscountAmount), 2),
            CashTotal = Math.Round(invoices.SelectMany(i => i.Payments)
                .Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount), 2),
            CardTotal = Math.Round(invoices.SelectMany(i => i.Payments)
                .Where(p => p.Method == PaymentMethod.Card).Sum(p => p.Amount), 2),
            RefundTotal = Math.Round(Math.Abs(refunds.Sum(i => i.TotalAmount)), 2),
            TaxBands = taxBands,
            Hourly = hourly,
            TopProducts = topProducts
        };
    }

    public async Task<IReadOnlyList<DailyReport>> GetRangeAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from)
        {
            (from, to) = (to, from);
        }

        var reports = new List<DailyReport>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            reports.Add(await GetDailyReportAsync(day, ct).ConfigureAwait(false));
        }
        return reports;
    }

    public async Task<IReadOnlyList<InvoiceRow>> GetInvoiceHistoryAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var start = from.ToDateTime(TimeOnly.MinValue).ToUniversalTime();
        var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue).ToUniversalTime();

        return await db.Invoices
            .AsNoTracking()
            .Where(i => i.CreatedAtUtc >= start && i.CreatedAtUtc < end)
            .OrderByDescending(i => i.CreatedAtUtc)
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

    /// <summary>
    /// Accountant-friendly CSV. Written with <c>StreamWriter</c> + <c>WriteLineAsync</c> so a
    /// multi-year export never blocks the dispatcher; uses invariant decimals for Excel.
    /// </summary>
    public async Task<string> ExportRangeCsvAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var reports = await GetRangeAsync(from, to, ct).ConfigureAwait(false);

        var path = Path.Combine(
            DatabaseConnectionFactory.ReportsDirectory,
            $"sales-{from:yyyyMMdd}-{to:yyyyMMdd}.csv");

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        await writer.WriteLineAsync("Date,Invoices,Gross,Net,Tax,Discount,Cash,Card,Refunds").ConfigureAwait(false);

        foreach (var r in reports)
        {
            await writer.WriteLineAsync(string.Join(',',
                r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.InvoiceCount.ToString(CultureInfo.InvariantCulture),
                r.GrossSales.ToString("0.00", CultureInfo.InvariantCulture),
                r.NetSales.ToString("0.00", CultureInfo.InvariantCulture),
                r.TaxTotal.ToString("0.00", CultureInfo.InvariantCulture),
                r.DiscountTotal.ToString("0.00", CultureInfo.InvariantCulture),
                r.CashTotal.ToString("0.00", CultureInfo.InvariantCulture),
                r.CardTotal.ToString("0.00", CultureInfo.InvariantCulture),
                r.RefundTotal.ToString("0.00", CultureInfo.InvariantCulture))).ConfigureAwait(false);
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
        return path;
    }
}
