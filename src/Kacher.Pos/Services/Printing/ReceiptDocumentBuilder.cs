using System.Globalization;
using Kacher.Pos.Models;
using Kacher.Pos.Services.Hardware.EscPos;

namespace Kacher.Pos.Services.Printing;

/// <summary>
/// Turns domain objects into finished ESC/POS byte streams: a customer receipt, a Z-report, a test
/// page and a bare drawer pulse.
/// </summary>
/// <remarks>
/// The builder is deliberately pure and static: given the same invoice and profile it always emits
/// the same bytes, which is what makes reprints and audit comparisons meaningful. Nothing here
/// touches IO — the transport layer owns sockets, spoolers and ports.
/// </remarks>
public static class ReceiptDocumentBuilder
{
    private const string Currency = "EGP";

    private static readonly CultureInfo MoneyCulture = CultureInfo.InvariantCulture;

    /// <summary>Logo shipped with the app; a store can replace it in place.</summary>
    private static string LogoPath => Path.Combine(AppContext.BaseDirectory, "Assets", "kacher.png");

    // ── Customer receipt ──────────────────────────────────────────────────────────────────────

    /// <param name="invoice">Invoice with <c>Items</c> and <c>Payments</c> populated.</param>
    /// <param name="config">Active hardware profile (roll width, code page, store identity).</param>
    /// <param name="cashierName">Resolved by the caller; the invoice only stores the id.</param>
    /// <param name="openDrawer">True when the drawer policy allows a kick for this sale.</param>
    public static byte[] BuildSaleReceipt(
        Invoice invoice,
        HardwareConfig config,
        string? cashierName,
        Shift? shift,
        bool openDrawer)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(config);

        var b = CreateBuilder(config);
        var isRefund = invoice.Status == InvoiceStatus.Refunded || invoice.RefundedInvoiceId is not null;

        WriteHeader(b, config, isRefund ? "REFUND RECEIPT" : "SALES RECEIPT");
        WriteInvoiceMeta(b, invoice, cashierName);

        if (!string.IsNullOrWhiteSpace(shift?.ShiftNumber))
        {
            b.TwoColumn("Shift", shift!.ShiftNumber);
        }

        b.Rule();

        // Column captions in Font B would be narrower than the value columns, so keep Font A.
        b.Bold().ThreeColumn("ITEM", "QTY", "AMOUNT").Bold(false);

        foreach (var item in invoice.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.ProductName))
            {
                b.Paragraph(item.ProductName);
            }

            // Line amounts are gross (quantity x unit price, ex-tax) so that the discount line
            // underneath visibly reconciles with the subtotal block.
            var qty = item.Quantity.ToString("0.##", MoneyCulture);
            b.ThreeColumn($"  {qty} x {Format(item.UnitPrice)}", string.Empty, Format(item.Quantity * item.UnitPrice));

            if (item.DiscountAmount > 0)
            {
                b.ThreeColumn("  line discount", string.Empty, "-" + Format(item.DiscountAmount));
            }
        }

        b.Rule();
        WriteTotals(b, invoice);
        WriteTender(b, invoice);

        if (!string.IsNullOrWhiteSpace(invoice.Note))
        {
            b.Rule().Paragraph("Note: " + invoice.Note);
        }

        WriteFooter(b, config);

        // Symbols last so they never split the totals block across a page break.
        if (config.PrintReceiptBarcode && !string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
        {
            b.Align(TextAlignment.Centre).Barcode(invoice.InvoiceNumber).Feed();
        }

        if (config.PrintQrCode && !string.IsNullOrWhiteSpace(config.DigitalReceiptBaseUrl))
        {
            var url = config.DigitalReceiptBaseUrl!.TrimEnd('/') + "/" + invoice.InvoiceNumber;
            b.Align(TextAlignment.Centre).Line("Scan for your digital receipt").QrCode(url, 5);
        }

        b.Align(TextAlignment.Left).Feed(2);

        if (config.AutoCut)
        {
            b.Cut();
        }

        // The drawer kick is appended to the job rather than sent separately for USB/spooler
        // printers: some drivers discard a zero-length follow-up job.
        if (openDrawer)
        {
            b.OpenDrawer(DrawerCommandParser.ParseOrDefault(config.OpenDrawerCommand));
        }

        return b.ToArray();
    }

    // ── Z-report ─────────────────────────────────────────────────────────────────────────────

    /// <summary>End-of-shift report: trade, tender mix and the cash reconciliation block.</summary>
    public static byte[] BuildZReport(ShiftSummary summary, HardwareConfig config)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(config);

        var b = CreateBuilder(config);

        WriteHeader(b, config, "Z-REPORT");

        b.Align(TextAlignment.Centre)
            .Line("End of shift / end of day")
            .Feed(1)
            .Align(TextAlignment.Left);

        b.TwoColumn("Shift", summary.ShiftNumber);
        b.TwoColumn("Cashier", summary.CashierName);
        b.TwoColumn("Opened", summary.OpenedAt.ToString("dd/MM/yy HH:mm", MoneyCulture));
        b.TwoColumn("Closed", summary.ClosedAt.ToString("dd/MM/yy HH:mm", MoneyCulture));
        b.Rule();

        b.Bold().Line("TRADE").Bold(false);
        b.TwoColumn("Invoices", summary.InvoiceCount.ToString(MoneyCulture));
        b.TwoColumn("Items sold", summary.ItemCount.ToString(MoneyCulture));
        b.TwoColumn("Gross sales", Format(summary.GrossSales));
        b.TwoColumn("Discounts", "-" + Format(summary.DiscountTotal));
        b.TwoColumn("Refunds", "-" + Format(summary.RefundTotal));
        b.TwoColumn("Net sales", Format(summary.NetSales));
        b.TwoColumn("Tax (VAT)", Format(summary.TaxTotal));
        b.TwoColumn("Average basket", Format(summary.AverageBasket));

        if (summary.TaxBands.Count > 0)
        {
            b.Rule();
            b.Bold().Line("TAX BREAKDOWN").Bold(false);

            foreach (var band in summary.TaxBands)
            {
                b.TwoColumn($" {band.Label} net", Format(band.NetAmount));
                b.TwoColumn($" {band.Label} tax", Format(band.TaxAmount));
            }
        }

        b.Rule();
        b.Bold().Line("TENDER").Bold(false);
        b.TwoColumn("Cash", Format(summary.CashTotal));
        b.TwoColumn("Card", Format(summary.CardTotal));
        b.TwoColumn("Split", Format(summary.SplitTotal));

        b.Rule();
        b.Bold().Line("CASH RECONCILIATION").Bold(false);
        b.TwoColumn("Opening float", Format(summary.OpeningFloat));
        b.TwoColumn("Cash sales", Format(summary.CashTotal));
        b.TwoColumn("Paid in", Format(summary.PaidIn));
        b.TwoColumn("Paid out", "-" + Format(summary.PaidOut));
        b.TwoColumn("Expected in drawer", Format(summary.ExpectedCash));
        b.TwoColumn("Counted", Format(summary.CountedCash));

        b.Bold();
        b.TwoColumn("DIFFERENCE", Format(summary.CashDifference));
        b.Bold(false);

        b.Line(summary.CashDifference switch
        {
            > 0 => "(overage — check for unrecorded cash sales)",
            < 0 => "(shortage — recount the drawer before banking)",
            _ => "(drawer balanced)"
        });

        WriteFooter(b, config);
        b.Feed(2);

        if (config.AutoCut)
        {
            b.Cut();
        }

        return b.ToArray();
    }

    // ── Commissioning test page ──────────────────────────────────────────────────────────────

    /// <summary>Diagnostic print used by the hardware settings screen.</summary>
    public static byte[] BuildTestPage(HardwareConfig config, DateTime nowLocal)
    {
        ArgumentNullException.ThrowIfNull(config);

        var b = CreateBuilder(config);

        WriteHeader(b, config, "HARDWARE TEST");
        b.TwoColumn("Printed", nowLocal.ToString("dd/MM/yyyy HH:mm:ss", MoneyCulture));
        b.TwoColumn("Roll", config.PaperWidth == PaperWidth.Mm58 ? "58 mm / 32 col" : "80 mm / 48 col");
        b.TwoColumn("Transport", config.ConnectionType.ToString());
        b.TwoColumn("Code page", config.CodePage.ToString(MoneyCulture));
        b.Rule();

        b.Line("Left aligned").Align(TextAlignment.Centre).Line("Centred").Align(TextAlignment.Right)
            .Line("Right aligned").Align(TextAlignment.Left);

        b.Bold().Line("Bold text").Bold(false);
        b.Size(1, 2).Line("Double height").Size();
        b.Size(2, 2).Line("Double width").Size();
        b.Underline().Line("Underline").Underline(false);

        b.Rule().Line("0123456789".PadRight(b.Columns, '.'));
        b.Line(new string('=', b.Columns));

        if (config.PrintReceiptBarcode)
        {
            b.Align(TextAlignment.Centre).Barcode("KACHER-POS-TEST").Feed();
        }

        if (config.PrintQrCode)
        {
            b.Align(TextAlignment.Centre).QrCode("https://kacher.local/hardware-test", 6);
        }

        b.Align(TextAlignment.Left).Feed(1);
        b.Paragraph("If this page is legible and cut cleanly, the printer profile is correct.");
        b.Feed(2);

        if (config.AutoCut)
        {
            b.Cut();
        }

        return b.ToArray();
    }

    // ── Sections ────────────────────────────────────────────────────────────────────────────

    private static EscPosBuilder CreateBuilder(HardwareConfig config)
    {
        var columns = EscPosEncoding.ColumnsFor(config.PaperWidth);
        var encoding = EscPosEncoding.ForPrinter(config.CodePage);

        return new EscPosBuilder(encoding, columns).Initialise(config.CodePage);
    }

    private static void WriteHeader(EscPosBuilder b, HardwareConfig config, string documentTitle)
    {
        b.Align(TextAlignment.Centre);

        if (config.PrintLogo)
        {
            // The raster is cached per (file, dot width), so repeated receipts pay the decode cost
            // exactly once. A missing logo degrades to a text-only header.
            var logo = LogoRasterizer.FromFile(LogoPath, EscPosEncoding.DotsFor(config.PaperWidth));

            if (logo is not null)
            {
                b.Logo(logo.WidthDots, logo.HeightDots, logo.Data);
            }
        }

        // Store name is the only element allowed to use double-width text: it must fit 24 chars.
        var name = config.StoreName?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            if (name.Length <= b.Columns / 2)
            {
                b.Size(2, 2).Bold().Line(name).Size().Bold(false);
            }
            else
            {
                b.Bold().Paragraph(name, string.Empty).Bold(false);
            }
        }

        if (!string.IsNullOrWhiteSpace(config.StoreAddress))
        {
            b.Paragraph(config.StoreAddress);
        }

        if (!string.IsNullOrWhiteSpace(config.StorePhone))
        {
            b.Line("Tel " + config.StorePhone);
        }

        if (!string.IsNullOrWhiteSpace(config.TaxRegistrationNumber))
        {
            b.Line("Tax Reg " + config.TaxRegistrationNumber);
        }

        b.Feed(1).Bold().Line(documentTitle).Bold(false);
        b.Align(TextAlignment.Left);
    }

    private static void WriteInvoiceMeta(EscPosBuilder b, Invoice invoice, string? cashierName)
    {
        // Persisted timestamps are UTC; every human-facing surface shows local time.
        var local = invoice.CreatedAtUtc.ToLocalTime();

        b.TwoColumn("Invoice", invoice.InvoiceNumber);
        b.TwoColumn("Date", local.ToString("dd/MM/yyyy HH:mm", MoneyCulture));

        if (!string.IsNullOrWhiteSpace(cashierName))
        {
            b.TwoColumn("Cashier", cashierName);
        }
    }

    private static void WriteTotals(EscPosBuilder b, Invoice invoice)
    {
        // Invoice.Subtotal is already net of every discount (line + invoice level), so gross is
        // reconstructed by adding the discount back. Keeping these two figures explicit means the
        // ticket reconciles line by line: gross - discounts = net, net + VAT = total.
        var gross = invoice.Subtotal + invoice.DiscountAmount;
        b.TwoColumn("Subtotal", Format(gross));

        if (invoice.DiscountAmount > 0)
        {
            b.TwoColumn("Discount", "-" + Format(invoice.DiscountAmount));
            b.TwoColumn("Net", Format(invoice.Subtotal));
        }

        // Tax bands: at most three in practice (0 %, 14 %, 5 %), so no scrolling risk.
        foreach (var band in invoice.Items
                             .GroupBy(i => i.TaxRate)
                             .Where(g => g.Key > 0)
                             .OrderBy(g => g.Key))
        {
            var label = (band.Key * 100).ToString("0.##", MoneyCulture) + "%";
            b.TwoColumn("VAT " + label, Format(band.Sum(i => i.TaxAmount)));
        }

        b.Rule('=');
        b.Size(1, 2).Bold().TwoColumn("TOTAL " + Currency, Format(invoice.TotalAmount)).Size().Bold(false);
        b.Rule('=');
    }

    private static void WriteTender(EscPosBuilder b, Invoice invoice)
    {
        b.TwoColumn("Paid by", invoice.PaymentMethod.ToString().ToUpperInvariant());

        if (invoice.PaymentMethod is PaymentMethod.Cash or PaymentMethod.Split && invoice.AmountTendered > 0)
        {
            b.TwoColumn("Tendered", Format(invoice.AmountTendered));
            b.TwoColumn("Change", Format(invoice.ChangeGiven));
        }

        if (invoice.Payments.Count > 1)
        {
            foreach (var payment in invoice.Payments)
            {
                b.TwoColumn("  " + payment.Method, Format(payment.Amount));
            }
        }
    }

    private static void WriteFooter(EscPosBuilder b, HardwareConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ReceiptFooter))
        {
            return;
        }

        b.Rule().Align(TextAlignment.Centre).Paragraph(config.ReceiptFooter).Align(TextAlignment.Left);
    }

    /// <summary>Money is always rendered with an invariant dot separator, never the OS culture.</summary>
    private static string Format(decimal value) => value.ToString("0.00", MoneyCulture);
}
