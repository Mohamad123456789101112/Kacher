using System.Globalization;
using System.IO;
using Kacher.Pos.Data;
using Kacher.Pos.Models;
using Kacher.Pos.Services.Printing;

namespace Kacher.Pos.Services.Pdf;

public interface IPdfReceiptService
{
    Task<string> CreateInvoicePdfAsync(Invoice invoice, HardwareConfig config, string? cashierName, CancellationToken ct = default);

    Task<string> CreateDailyReportPdfAsync(DailyReport report, HardwareConfig config, CancellationToken ct = default);

    /// <summary>Z-report as an A4/thermal PDF, mirroring the ESC/POS Z-report layout.</summary>
    Task<string> CreateShiftSummaryPdfAsync(ShiftSummary summary, HardwareConfig config, CancellationToken ct = default);
}

/// <summary>
/// Renders receipts and daily summaries to PDF using <see cref="SimplePdfWriter"/>.
/// Receipt pages are sized to the roll (80 mm = 226.8 pt) so an emailed PDF looks exactly like the
/// paper ticket; report pages are A4.
/// </summary>
public sealed class PdfReceiptService : IPdfReceiptService
{
    private const double Margin = 10;
    private const double LineHeight = 10.5;
    private const double BodySize = 8;
    private const double HeaderSize = 12;

    public async Task<string> CreateInvoicePdfAsync(
        Invoice invoice, HardwareConfig config, string? cashierName, CancellationToken ct = default)
    {
        var width = config.PaperWidth == PaperWidth.Mm58 ? 164.4 : 226.8;
        var innerWidth = width - (Margin * 2);

        // Approximate advance width of Helvetica at 8 pt (0.5 em) — good enough for a ticket.
        var maxChars = Math.Max(24, (int)(innerWidth / (BodySize * 0.5)));

        var lines = new List<PdfTextLine>();

        // Layout top-down from y = 0; the whole block is shifted to positive user space afterwards
        // because PDF's origin is the bottom-left corner of the page.
        var cursor = new LayoutCursor(startY: 0);

        void Text(string text, bool bold = false, double size = BodySize, double? x = null, double? atY = null)
        {
            lines.Add(new PdfTextLine(text, x ?? Margin, atY ?? cursor.Next(size), size, bold));
        }

        void Centre(string text, bool bold = false, double size = BodySize)
            => lines.Add(new PdfTextLine(text, width / 2 - (text.Length * size * 0.25), cursor.Next(size), size, bold));

        void Pair(string left, string right, bool bold = false)
        {
            var size = BodySize;
            lines.Add(new PdfTextLine(left, Margin, cursor.Next(size), size, bold));
            lines.Add(new PdfTextLine(right, width - Margin - (right.Length * size * 0.5), cursor.Same(), size, bold));
        }

        void Rule(char c = '-') => Text(new string(c, maxChars));

        Centre(config.StoreName, bold: true, size: HeaderSize);
        if (!string.IsNullOrWhiteSpace(config.StoreAddress)) Centre(config.StoreAddress!);
        if (!string.IsNullOrWhiteSpace(config.StorePhone)) Centre($"Tel: {config.StorePhone}");
        if (!string.IsNullOrWhiteSpace(config.TaxRegistrationNumber)) Centre($"Tax Reg: {config.TaxRegistrationNumber}");

        Rule('=');
        Pair("Invoice", invoice.InvoiceNumber);
        Pair("Date", invoice.CreatedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(cashierName)) Pair("Cashier", cashierName);
        Rule();

        foreach (var item in invoice.Items)
        {
            foreach (var nameLine in Wrap(item.ProductName, maxChars - 2))
            {
                Text(" " + nameLine);
            }

            var detail = $"{item.Quantity.ToString("0.##", CultureInfo.InvariantCulture)} x " +
                         $"{item.UnitPrice.ToString("0.00", CultureInfo.InvariantCulture)}";
            Pair("  " + detail, (item.TotalPrice + item.TaxAmount).ToString("0.00", CultureInfo.InvariantCulture));
        }

        Rule();
        Pair("Subtotal", invoice.Subtotal.ToString("0.00", CultureInfo.InvariantCulture));

        if (invoice.DiscountAmount > 0)
        {
            Pair("Discount", "-" + invoice.DiscountAmount.ToString("0.00", CultureInfo.InvariantCulture));
        }

        foreach (var band in invoice.Items.Where(i => i.TaxAmount != 0).GroupBy(i => i.TaxRate))
        {
            Pair($"Tax {band.Key * 100:0.##}%", band.Sum(i => i.TaxAmount).ToString("0.00", CultureInfo.InvariantCulture));
        }

        Rule('=');
        Pair("TOTAL", $"{invoice.TotalAmount:0.00} EGP", bold: true);
        Pair("Paid by", invoice.PaymentMethod.ToString().ToUpperInvariant());

        if (invoice.AmountTendered > 0)
        {
            Pair("Tendered", invoice.AmountTendered.ToString("0.00", CultureInfo.InvariantCulture));
        }

        if (invoice.ChangeGiven > 0)
        {
            Pair("CHANGE", invoice.ChangeGiven.ToString("0.00", CultureInfo.InvariantCulture), bold: true);
        }

        Rule();
        if (!string.IsNullOrWhiteSpace(config.ReceiptFooter))
        {
            foreach (var footerLine in Wrap(config.ReceiptFooter!, maxChars))
            {
                Centre(footerLine);
            }
        }

        Centre("Reprinted from Kacher POS", size: 7);

        var (pageLines, pageHeight) = Normalize(lines, Margin);
        var page = new PdfPage(width, pageHeight, pageLines);

        var path = Path.Combine(DatabaseConnectionFactory.ReportsDirectory, $"{invoice.InvoiceNumber}.pdf");
        await SimplePdfWriter.WriteAsync(path, new[] { page }, ct).ConfigureAwait(false);
        return path;
    }

    public async Task<string> CreateDailyReportPdfAsync(DailyReport report, HardwareConfig config, CancellationToken ct = default)
    {
        const double width = 595;   // A4 portrait, points
        const double height = 842;
        const double margin = 40;
        const double size = 10;
        var cursor = new LayoutCursor(startY: height - margin);
        var lines = new List<PdfTextLine>();

        void Line(string text, bool bold = false, double fontSize = size, double x = margin)
            => lines.Add(new PdfTextLine(text, x, cursor.Next(fontSize), fontSize, bold));

        void Row(string left, string right, bool bold = false)
        {
            Line(left, bold);
            lines.Add(new PdfTextLine(right, width - margin - (right.Length * size * 0.5), cursor.Same(), size, bold));
        }

        Line(config.StoreName, bold: true, fontSize: 18);
        Line("Daily Sales Summary", fontSize: 13);
        Line($"Business date: {report.Date:dd MMM yyyy}    Generated: {DateTime.Now:dd MMM yyyy HH:mm}");
        cursor.Skip(8);

        Line("Sales", bold: true, fontSize: 12);
        Row("Invoices", report.InvoiceCount.ToString(CultureInfo.InvariantCulture));
        Row("Gross sales", $"{report.GrossSales:0.00} EGP");
        Row("Net sales (excl. tax)", $"{report.NetSales:0.00} EGP");
        Row("Discounts", $"{report.DiscountTotal:0.00} EGP");
        Row("Refunds", $"{report.RefundTotal:0.00} EGP");
        Row("Tax collected", $"{report.TaxTotal:0.00} EGP");
        Row("Average basket", $"{report.AverageBasket:0.00} EGP", bold: true);
        cursor.Skip(10);

        Line("Tenders", bold: true, fontSize: 12);
        Row("Cash", $"{report.CashTotal:0.00} EGP");
        Row("Card", $"{report.CardTotal:0.00} EGP");
        cursor.Skip(10);

        if (report.TaxBands.Count > 0)
        {
            Line("Tax breakdown", bold: true, fontSize: 12);
            foreach (var band in report.TaxBands)
            {
                Row($"{band.Label} — net {band.NetAmount:0.00}", $"{band.TaxAmount:0.00} EGP");
            }
            cursor.Skip(10);
        }

        if (report.TopProducts.Count > 0)
        {
            Line("Top products", bold: true, fontSize: 12);
            var rank = 1;
            foreach (var product in report.TopProducts)
            {
                Row($"{rank++}. {product.Name}  x{product.Quantity:0.##}", $"{product.Revenue:0.00} EGP");
            }
            cursor.Skip(10);
        }

        if (report.Hourly.Count > 0)
        {
            Line("Hourly revenue", bold: true, fontSize: 12);
            const double chartLeft = margin + 60;
            const double chartWidth = width - margin * 2 - 80;
            var max = report.Hourly.Max(h => h.Revenue);

            foreach (var hour in report.Hourly)
            {
                Line($"{hour.Hour:00}:00", x: margin);
                var barWidth = max <= 0 ? 0 : (double)(hour.Revenue / max) * chartWidth;
                lines.Add(new PdfTextLine(new string('#', Math.Max(1, (int)(barWidth / 4))),
                    chartLeft, cursor.Same(), size));
                lines.Add(new PdfTextLine($"{hour.Revenue:0.00}", width - margin - 40, cursor.Same(), size));
                cursor.Skip(size);
            }
        }

        cursor.Skip(14);
        Line("Z-Report signature: ______________________", fontSize: 9);
        Line($"Hardware profile: {config.ProfileName} ({ReceiptDocumentBuilder.DescribeTarget(config)})", fontSize: 8);

        var path = Path.Combine(DatabaseConnectionFactory.ReportsDirectory, $"daily-{report.Date:yyyyMMdd}.pdf");
        await SimplePdfWriter.WriteAsync(path, new[] { new PdfPage(width, height, lines) }, ct).ConfigureAwait(false);
        return path;
    }

    public async Task<string> CreateShiftSummaryPdfAsync(
        ShiftSummary summary, HardwareConfig config, CancellationToken ct = default)
    {
        const double width = 420;
        const double margin = 24;
        const double size = 9;

        var cursor = new LayoutCursor(startY: 0);
        var lines = new List<PdfTextLine>();
        var rightEdge = width - margin;

        void Line(string text, bool bold = false, double fontSize = size, double x = margin)
            => lines.Add(new PdfTextLine(text, x, cursor.Next(fontSize), fontSize, bold));

        void Row(string left, string right, bool bold = false)
        {
            Line(left, bold);
            lines.Add(new PdfTextLine(right, rightEdge - (right.Length * size * 0.5), cursor.Same(), size, bold));
        }

        void Rule() => Line(new string('-', (int)((width - (margin * 2)) / (size * 0.5))));

        Line(config.StoreName, bold: true, fontSize: 16);
        Line("SHIFT CLOSING REPORT (Z-REPORT)", bold: true, fontSize: 12);
        Rule();
        Row("Shift", summary.ShiftNumber);
        Row("Cashier", summary.CashierName);
        Row("Opened", summary.OpenedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));
        Row("Closed", summary.ClosedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));
        Rule();

        Row("Invoices", summary.InvoiceCount.ToString(CultureInfo.InvariantCulture));
        Row("Items sold", summary.ItemCount.ToString(CultureInfo.InvariantCulture));
        Row("Gross sales", $"{summary.GrossSales:0.00} EGP");
        Row("Net sales", $"{summary.NetSales:0.00} EGP");
        Row("Discounts", $"{summary.DiscountTotal:0.00} EGP");
        Row("Refunds", $"{summary.RefundTotal:0.00} EGP");
        Row("Tax collected", $"{summary.TaxTotal:0.00} EGP");
        Rule();

        Row("Cash", $"{summary.CashTotal:0.00} EGP");
        Row("Card", $"{summary.CardTotal:0.00} EGP");
        Rule();

        Row("Opening float", $"{summary.OpeningFloat:0.00} EGP");
        Row("Paid in", $"{summary.PaidIn:0.00} EGP");
        Row("Paid out", $"{summary.PaidOut:0.00} EGP");
        Row("Expected drawer", $"{summary.ExpectedCash:0.00} EGP");
        Row("Counted", $"{summary.CountedCash:0.00} EGP");
        Row("Difference", $"{summary.CashDifference:0.00} EGP", bold: true);
        Rule();
        Row("Average basket", $"{summary.AverageBasket:0.00} EGP");
        cursor.Skip(16);
        Line("Cashier signature: ______________________", fontSize: 8);
        Line("Manager signature: ______________________", fontSize: 8);

        var (pageLines, pageHeight) = Normalize(lines, margin);

        var path = Path.Combine(
            DatabaseConnectionFactory.ReportsDirectory,
            $"zreport-{summary.ShiftNumber.Replace('/', '-')}.pdf");

        await SimplePdfWriter.WriteAsync(path, new[] { new PdfPage(width, pageHeight, pageLines) }, ct)
            .ConfigureAwait(false);

        return path;
    }

    /// <summary>Shifts a top-down layout into PDF user space and returns the required page height.</summary>
    private static (IReadOnlyList<PdfTextLine> Lines, double Height) Normalize(
        IReadOnlyList<PdfTextLine> lines, double margin)
    {
        if (lines.Count == 0)
        {
            return (Array.Empty<PdfTextLine>(), 120);
        }

        var shift = margin - lines.Min(l => l.Y);
        var shifted = lines.Select(l => l with { Y = l.Y + shift }).ToList();
        return (shifted, shifted.Max(l => l.Y) + margin + LineHeight);
    }

    public static IReadOnlyList<string> Wrap(string text, int maxChars)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > maxChars)
            {
                result.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(word);

            while (current.Length > maxChars)
            {
                result.Add(current.ToString(0, maxChars));
                current.Remove(0, maxChars);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result.Count == 0 ? new[] { string.Empty } : result;
    }

    /// <summary>Tracks the current baseline while emitting lines; PDF origin is bottom-left.</summary>
    private sealed class LayoutCursor
    {
        public LayoutCursor(double startY = 0) => Position = startY;

        public double Position { get; private set; }

        private double _last;

        public double Next(double fontSize)
        {
            _last = Position;
            Position -= LineHeight;
            return _last;
        }

        public double Same() => _last;

        public void Skip(double amount) => Position -= amount;
    }
}
