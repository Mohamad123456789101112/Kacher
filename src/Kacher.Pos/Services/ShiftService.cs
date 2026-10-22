using System.Text.Json;
using Kacher.Pos.Data;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Microsoft.EntityFrameworkCore;

namespace Kacher.Pos.Services;

public interface IShiftService
{
    Task<Shift?> GetOpenShiftAsync(CancellationToken ct = default);

    Task<Shift> OpenShiftAsync(int userId, decimal openingFloat, CancellationToken ct = default);

    Task<ShiftSummary> CloseShiftAsync(int shiftId, decimal countedCash, string? note, CancellationToken ct = default);

    Task<ShiftSummary> BuildSummaryAsync(int shiftId, decimal? countedCash = null, CancellationToken ct = default);

    Task AddCashMovementAsync(int shiftId, CashMovementKind kind, decimal amount, string? reason, CancellationToken ct = default);
}

public sealed class ShiftService : IShiftService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly IDbContextFactory<KacherDbContext> _factory;
    private readonly IClock _clock;
    private readonly IAppLog _log;

    public ShiftService(IDbContextFactory<KacherDbContext> factory, IClock clock, IAppLog log)
    {
        _factory = factory;
        _clock = clock;
        _log = log;
    }

    public async Task<Shift?> GetOpenShiftAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Shifts
            .AsNoTracking()
            .Include(s => s.User)
            .Where(s => s.Status == ShiftStatus.Open)
            .OrderByDescending(s => s.OpenedAtUtc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<Shift> OpenShiftAsync(int userId, decimal openingFloat, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var alreadyOpen = await db.Shifts
            .AnyAsync(s => s.Status == ShiftStatus.Open, ct)
            .ConfigureAwait(false);

        if (alreadyOpen)
        {
            throw new InvalidOperationException("A shift is already open. Close it before opening a new one.");
        }

        var shift = new Shift
        {
            ShiftNumber = $"S-{_clock.Now:yyyyMMdd-HHmm}",
            UserId = userId,
            OpeningFloat = openingFloat,
            OpenedAtUtc = _clock.UtcNow,
            Status = ShiftStatus.Open
        };

        db.Shifts.Add(shift);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _log.Info($"Shift {shift.ShiftNumber} opened with float {openingFloat:0.00}.");
        return shift;
    }

    /// <summary>
    /// Closes the drawer session. Everything is computed from persisted invoices inside one
    /// transaction so the Z-report can never disagree with the sales history.
    /// </summary>
    public async Task<ShiftSummary> CloseShiftAsync(
        int shiftId, decimal countedCash, string? note, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var shift = await db.Shifts
            .Include(s => s.User)
            .Include(s => s.CashMovements)
            .FirstOrDefaultAsync(s => s.Id == shiftId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Shift not found.");

        if (shift.Status == ShiftStatus.Closed)
        {
            throw new InvalidOperationException("Shift is already closed.");
        }

        var summary = await ComputeSummaryAsync(db, shift, countedCash, ct).ConfigureAwait(false);

        shift.Status = ShiftStatus.Closed;
        shift.ClosedAtUtc = _clock.UtcNow;
        shift.CountedCash = countedCash;
        shift.CashDifference = countedCash - summary.ExpectedCash;
        shift.ClosingNote = note;
        shift.ZReportJson = JsonSerializer.Serialize(summary, JsonOptions);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        _log.Info($"Shift {shift.ShiftNumber} closed. Expected {summary.ExpectedCash:0.00}, " +
                  $"counted {countedCash:0.00}, difference {shift.CashDifference:0.00}.");

        return summary;
    }

    public async Task<ShiftSummary> BuildSummaryAsync(int shiftId, decimal? countedCash = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var shift = await db.Shifts
            .Include(s => s.User)
            .Include(s => s.CashMovements)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == shiftId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Shift not found.");

        return await ComputeSummaryAsync(db, shift, countedCash ?? shift.CountedCash ?? 0m, ct).ConfigureAwait(false);
    }

    private static async Task<ShiftSummary> ComputeSummaryAsync(
        KacherDbContext db, Shift shift, decimal countedCash, CancellationToken ct)
    {
        var invoices = await db.Invoices
            .AsNoTracking()
            .Include(i => i.Items)
            .Include(i => i.Payments)
            .Where(i => i.ShiftId == shift.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var completed = invoices.Where(i => i.Status == InvoiceStatus.Completed && i.RefundedInvoiceId == null).ToList();
        var refunds = invoices.Where(i => i.RefundedInvoiceId != null).ToList();

        var cash = invoices.SelectMany(i => i.Payments)
            .Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
        var card = invoices.SelectMany(i => i.Payments)
            .Where(p => p.Method == PaymentMethod.Card).Sum(p => p.Amount);
        var split = invoices.SelectMany(i => i.Payments)
            .Where(p => p.Method == PaymentMethod.Split).Sum(p => p.Amount);

        var taxBands = invoices
            .SelectMany(i => i.Items)
            .GroupBy(i => i.TaxRate)
            .Select(g => new TaxBand(
                g.Key,
                Math.Round(g.Sum(x => x.TotalPrice), 2),
                Math.Round(g.Sum(x => x.TaxAmount), 2)))
            .OrderByDescending(b => b.Rate)
            .ToList();

        return new ShiftSummary
        {
            ShiftId = shift.Id,
            ShiftNumber = shift.ShiftNumber,
            CashierName = shift.User?.DisplayName ?? "—",
            OpenedAt = shift.OpenedAtUtc.ToLocalTime(),
            ClosedAt = shift.ClosedAtUtc?.ToLocalTime() ?? DateTime.Now,
            InvoiceCount = completed.Count,
            ItemCount = completed.Sum(i => i.Items.Count),
            GrossSales = Math.Round(invoices.Sum(i => i.TotalAmount), 2),
            NetSales = Math.Round(invoices.Sum(i => i.Subtotal), 2),
            TaxTotal = Math.Round(invoices.Sum(i => i.TaxAmount), 2),
            DiscountTotal = Math.Round(invoices.Sum(i => i.DiscountAmount), 2),
            RefundTotal = Math.Round(Math.Abs(refunds.Sum(i => i.TotalAmount)), 2),
            CashTotal = Math.Round(cash, 2),
            CardTotal = Math.Round(card, 2),
            SplitTotal = Math.Round(split, 2),
            OpeningFloat = shift.OpeningFloat,
            PaidIn = shift.CashMovements.Where(m => m.Kind == CashMovementKind.PaidIn).Sum(m => m.Amount),
            PaidOut = shift.CashMovements.Where(m => m.Kind is CashMovementKind.PaidOut or CashMovementKind.BankDrop).Sum(m => m.Amount),
            CountedCash = countedCash,
            CashDifference = Math.Round(countedCash - (shift.OpeningFloat + cash
                + shift.CashMovements.Where(m => m.Kind == CashMovementKind.PaidIn).Sum(m => m.Amount)
                - shift.CashMovements.Where(m => m.Kind is CashMovementKind.PaidOut or CashMovementKind.BankDrop).Sum(m => m.Amount)), 2),
            TaxBands = taxBands
        };
    }

    public async Task AddCashMovementAsync(
        int shiftId, CashMovementKind kind, decimal amount, string? reason, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.CashMovements.Add(new CashMovement
        {
            ShiftId = shiftId,
            Kind = kind,
            Amount = amount,
            Reason = reason,
            CreatedAtUtc = _clock.UtcNow
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
