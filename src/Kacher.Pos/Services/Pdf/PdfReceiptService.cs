using System.Globalization;
using System.Text;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;

namespace Kacher.Pos.Services.Pdf;

/// <summary>Writes printable documents (receipt, Z-report, daily report) as PDF files on disk.</summary>
public interface IPdfReceiptService
{
    /// <summary>Writes the customer receipt as a roll-shaped PDF and returns the full path.</summary>
    Task<string> CreateInvoicePdfAsync(Invoice invoice, HardwareConfig config, string? cashierName, CancellationToken ct = default);

    /// <summary>Writes the end-of-shift Z-report as A4 and returns the full path.</summary>
    Task<string> CreateShiftSummaryPdfAsync(ShiftSummary summary, HardwareConfig config, CancellationToken ct = default);

    /// <summary>Writes the daily trading report as A4 and returns the full path.</summary>
    Task<string> CreateDailyReportPdfAsync(DailyReport report, HardwareConfig config, CancellationToken ct = default);
}

/// <summary>
/// Builds PDFs with <see cref="SimplePdfWriter"/>: a narrow page that mimics the paper roll for
/// receipts, and A4 for management reports.
/// </summary>
/// <remarks>
/// Everything runs on the thread pool and the file is written with <c>WriteAllBytesAsync</c>, so a
/// 30-page daily report cannot stall the till. Failures surface as a message the caller shows in a
/// notification — a report is never a reason to block a sale.
/// </remarks>
public sealed class PdfReceiptService : IPdfReceiptService
{
    private const double A4Width = 595;
    private const double A4Height = 842;
    private const double Margin = 40;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly IAppLog _log;

    public PdfReceiptService(IAppLog log) => _log = log;

    // ── Receipt (roll-shaped page) ───────────────────────────────────────────────────────────

    public async Task<string> CreateInvoicePdfAsync(Invoice invoice, HardwareConfig config, string? cashierName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(config);

        var width = config.PaperWidth == PaperWidth.Mm58 ? 164 : 227;

        // Estimate the height from the content so the page is exactly as tall as the ticket.
        var lines = 18 + (invoice.Items.Count * 2) + (invoice.Payments.Count * 2);
        var height = Math.Max(240, lines * 11.5);

        var writer = new SimplePdfWriter(width, height);
        var y = 14.0;
        var left = 10.0;
        var right = width - 10.0;
        var columns = config.ReceiptColumns;

        void Row(string text, bool bold = false, double size = 7.5)
        {
            writer.Text(left, y, Shorten(text, columns), size, bold);
            y += size + 3;
        }

        void Pair(string label, string value, bool bold = false, double size = 7.5)
        {
            writer.Text(left, y, label, size, bold);
            writer.TextRight(value, y, size, bold);
            y += size + 3;
        }

        void Divider(char c = '-') => Row(new string(c, columns));

        writer.TextCentre(config.StoreName, y, 11, bold: true);
        y += 15;

        foreach (var line in new[] { config.StoreAddress, config.StorePhone })
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                writer.TextCentre(line, y, 7);
                y += 10;
            }
        }

        if (!string.IsNullOrWhiteSpace(config.TaxRegistrationNumber))
        {
            writer.TextCentre("Tax Reg " + config.TaxRegistrationNumber, y, 7);
            y += 10;
        }

        y += 4;
        Divider('=');

        Pair("Invoice", invoice.InvoiceNumber);
        Pair("Date", invoice.CreatedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Invariant));

        if (!string.IsNullOrWhiteSpace(cashierName))
        {
            Pair("Cashier", cashierName);
        }

        Divider();

        foreach (var item in invoice.Items)
        {
            Row(item.ProductName);
            Pair($"  {item.Quantity.ToString("0.##", Invariant)} x {item.UnitPrice.ToString("0.00", Invariant)}",
                item.TotalPrice.ToString("0.00", Invariant));
        }

        Divider();
        Pair("Subtotal", (invoice.Subtotal + invoice.DiscountAmount).ToString("0.00", Invariant));

        if (invoice.DiscountAmount > 0)
        {
            Pair("Discount", "-" + invoice.DiscountAmount.ToString("0.00", Invariant));
            Pair("Net", invoice.Subtotal.ToString("0.00", Invariant));
        }

        foreach (var band in invoice.Items.Where(i => i.TaxRate > 0).GroupBy(i => i.TaxRate).OrderBy(g => g.Key))
        {
            var label = (band.Key * 100).ToString("0.##", Invariant) + "%";
            Pair("VAT " + label, band.Sum(i => i.TaxAmount).ToString("0.00", Invariant));
        }

        Divider('=');

        // Total on a shaded band, the way a paper ticket emphasises it.
        writer.Rectangle(left, y - 8, right - left, 16, 0.88);
        writer.Text(left + 2, y, "TOTAL EGP", 8.5, bold: true);
        writer.TextRight(invoice.TotalAmount.ToString("0.00", Invariant), y, 8.5, bold: true);
        y += 13;

        Pair("Paid by", invoice.PaymentMethod.ToString().ToUpperInvariant());

        if (invoice.AmountTendered > 0)
        {
            Pair("Tendered", invoice.AmountTendered.ToString("0.00", Invariant));
            Pair("Change", invoice.ChangeGiven.ToString("0.00", Invariant));
        }

        if (!string.IsNullOrWhiteSpace(config.ReceiptFooter))
        {
            y += 4;
            Divider();

            foreach (var line in Wrap(config.ReceiptFooter!, columns))
            {
                writer.TextCentre(line, y, 7);
                y += 9;
            }
        }

        var bytes = writer.Build($"Receipt {invoice.InvoiceNumber}");
        return await WriteAsync($"receipt-{invoice.InvoiceNumber}.pdf", bytes, ct).ConfigureAwait(false);
    }

    // ── Z-report ─────────────────────────────────────────────────────────────────────────────

    public async Task<string> CreateShiftSummaryPdfAsync(ShiftSummary summary, HardwareConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var writer = new SimplePdfWriter(A4Width, A4Height);

        writer.TextCentre(config.StoreName, 60, 16, bold: true);
        writer.TextCentre("Z-REPORT — end of shift", 80, 12, bold: true);

        var y = 120.0;
        y = WriteKeyValueBlock(writer, y,
            ("Shift", summary.ShiftNumber),
            ("Cashier", summary.CashierName),
            ("Opened", summary.OpenedAt.ToString("dd/MM/yyyy HH:mm", Invariant)),
            ("Closed", summary.ClosedAt.ToString("dd/MM/yyyy HH:mm", Invariant)));

        y += 10;
        y = WriteSectionTitle(writer, y, "Trade");
        y = WriteKeyValueBlock(writer, y,
            ("Invoices", summary.InvoiceCount.ToString(Invariant)),
            ("Items sold", summary.ItemCount.ToString(Invariant)),
            ("Gross sales", Money(summary.GrossSales)),
            ("Discounts", "-" + Money(summary.DiscountTotal)),
            ("Refunds", "-" + Money(summary.RefundTotal)),
            ("Net sales", Money(summary.NetSales)),
            ("VAT", Money(summary.TaxTotal)),
            ("Average basket", Money(summary.AverageBasket)));

        if (summary.TaxBands.Count > 0)
        {
            y += 10;
            y = WriteSectionTitle(writer, y, "Tax breakdown");
            y = WriteTable(
                writer,
                y,
                ["Rate", "Net", "Tax"],
                summary.TaxBands.Select(b => new[] { b.Label, Money(b.NetAmount), Money(b.TaxAmount) }).ToList());
        }

        y += 10;
        y = WriteSectionTitle(writer, y, "Tender mix");
        y = WriteKeyValueBlock(writer, y,
            ("Cash", Money(summary.CashTotal)),
            ("Card", Money(summary.CardTotal)),
            ("Split", Money(summary.SplitTotal)));

        y += 10;
        y = WriteSectionTitle(writer, y, "Cash reconciliation");
        y = WriteKeyValueBlock(writer, y,
            ("Opening float", Money(summary.OpeningFloat)),
            ("Cash sales", Money(summary.CashTotal)),
            ("Paid in", Money(summary.PaidIn)),
            ("Paid out", "-" + Money(summary.PaidOut)),
            ("Expected in drawer", Money(summary.ExpectedCash)),
            ("Counted", Money(summary.CountedCash)),
            ("Difference", Money(summary.CashDifference)));

        y += 20;
        writer.Text(Margin, y, summary.CashDifference == 0
            ? "Drawer balanced."
            : summary.CashDifference > 0
                ? "Overage — investigate before banking."
                : "Shortage — recount the drawer before banking.", 10, bold: true);

        y += 40;
        writer.Rule(y);
        writer.Text(Margin, y + 16, "Cashier signature", 8);
        writer.Text(A4Width / 2, y + 16, "Manager signature", 8);

        var bytes = writer.Build($"Z-report {summary.ShiftNumber}");
        return await WriteAsync($"zreport-{summary.ShiftNumber}.pdf", bytes, ct).ConfigureAwait(false);
    }

    // ── Daily report ─────────────────────────────────────────────────────────────────────────

    public async Task<string> CreateDailyReportPdfAsync(DailyReport report, HardwareConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var writer = new SimplePdfWriter(A4Width, A4Height);

        writer.TextCentre(config.StoreName, 60, 16, bold: true);
        writer.TextCentre($"Daily report — {report.Date:dd/MM/yyyy}", 80, 12, bold: true);

        var y = 125.0;

        y = WriteKeyValueBlock(writer, y,
            ("Invoices", report.InvoiceCount.ToString(Invariant)),
            ("Gross sales", Money(report.GrossSales)),
            ("Discounts", "-" + Money(report.DiscountTotal)),
            ("Refunds", "-" + Money(report.RefundTotal)),
            ("Net sales", Money(report.NetSales)),
            ("VAT", Money(report.TaxTotal)),
            ("Cash", Money(report.CashTotal)),
            ("Card", Money(report.CardTotal)),
            ("Average basket", Money(report.AverageBasket)));

        if (report.TopProducts.Count > 0)
        {
            y += 14;
            y = WriteSectionTitle(writer, y, "Top products");
            y = WriteTable(
                writer,
                y,
                ["Product", "Qty", "Revenue"],
                report.TopProducts.Take(15)
                    .Select(p => new[] { p.Name, p.Quantity.ToString("0.##", Invariant), Money(p.Revenue) })
                    .ToList());
        }

        if (report.Hourly.Count > 0)
        {
            y += 14;
            y = WriteSectionTitle(writer, y, "Sales by hour");
            y = WriteTable(
                writer,
                y,
                ["Hour", "Invoices", "Revenue"],
                report.Hourly.Select(h => new[]
                {
                    $"{h.Hour:00}:00",
                    h.InvoiceCount.ToString(Invariant),
                    Money(h.Revenue)
                }).ToList());
        }

        var bytes = writer.Build($"Daily report {report.Date:yyyy-MM-dd}");
        return await WriteAsync($"daily-{report.Date:yyyy-MM-dd}.pdf", bytes, ct).ConfigureAwait(false);
    }

    // ── Shared layout helpers ───────────────────────────────────────────────────────────────

    private static double WriteSectionTitle(SimplePdfWriter writer, double y, string title)
    {
        writer.Text(Margin, y, title.ToUpperInvariant(), 10, bold: true);
        y += 6;
        writer.Rule(y);
        return y + 16;
    }

    private static double WriteKeyValueBlock(SimplePdfWriter writer, double y, params (string Key, string Value)[] rows)
    {
        foreach (var (key, value) in rows)
        {
            writer.Text(Margin, y, key, 9);
            writer.TextRight(value, y, 9);
            y += 14;
        }

        return y;
    }

    private static double WriteTable(SimplePdfWriter writer, double y, string[] headers, List<string[]> rows)
    {
        writer.Text(Margin, y, headers[0], 8.5, bold: true);
        writer.TextRight(headers[^1], y, 8.5, bold: true);

        if (headers.Length == 3)
        {
            writer.Text(Math.Max(Margin + 90, writer.RightEdge - 110), y, headers[1], 8.5, bold: true);
        }

        y += 5;
        writer.Rule(y);
        y += 14;

        foreach (var row in rows)
        {
            writer.Text(Margin, y, Shorten(row[0], 46), 8.5);

            if (row.Length == 3)
            {
                writer.Text(Math.Max(Margin + 90, writer.RightEdge - 110), y, row[1], 8.5);
            }

            writer.TextRight(row[^1], y, 8.5);
            y += 13;
        }

        return y;
    }

    private async Task<string> WriteAsync(string fileName, byte[] bytes, CancellationToken ct)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Kacher",
            "Reports");

        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, fileName);
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);

        _log.Info($"PDF written: {path} ({bytes.Length} bytes).");
        return path;
    }

    private static string Money(decimal value) => value.ToString("0.00", Invariant) + " EGP";

    private static string Shorten(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..(max - 1)] + ".";

    private static IEnumerable<string> Wrap(string text, int width)
    {
        foreach (var raw in text.Split('\n'))
        {
            var current = new StringBuilder();

            foreach (var word in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.Length + word.Length + 1 > width)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(word);
            }

            yield return current.ToString();
        }
    }
}
