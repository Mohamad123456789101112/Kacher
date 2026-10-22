using System.Globalization;
using Kacher.Pos.Models;
using Kacher.Pos.Services.Hardware.EscPos;

namespace Kacher.Pos.Services.Printing;

/// <summary>
/// Turns domain objects into ESC/POS byte streams. All layout decisions (columns, emphasis,
/// paper width) live here so the transport stays a dumb pipe and the templates stay testable.
/// </summary>
public static class ReceiptDocumentBuilder
{
    private const string Currency = "EGP";

    // ── Sale receipt ─────────────────────────────────────────────────────────────────────────

    public static byte[] BuildSaleReceipt(
        Invoice invoice,
        HardwareConfig config,
        string? cashierName,
        string? shiftNumber,
        byte[]? logoRaster = null,
        int logoWidthBytes = 0,
        int logoHeightDots = 0)
    {
        var encoding = EscPosEncoding.ForIndex(config.CodePage);
        var b = new EscPosBuilder(config.ReceiptColumns, encoding).Init(config.CodePage);

        // ── Header: store identity ───────────────────────────────────────────────────────────
        if (logoRaster is not null && logoWidthBytes > 0 && logoHeightDots > 0)
        {
            b.AlignCenter()
             .RasterImage(logoWidthBytes, logoHeightDots, logoRaster)
             .Feed(1);
        }

        b.AlignCenter()
         .Bold().Size(2, 2).Line(config.StoreName).Size(1, 1).Bold(false);

        if (!string.IsNullOrWhiteSpace(config.StoreAddress))
        {
            b.Wrap(config.StoreAddress!, center: true);
        }

        if (!string.IsNullOrWhiteSpace(config.StorePhone))
        {
            b.Line($"Tel: {config.StorePhone}");
        }

        if (!string.IsNullOrWhiteSpace(config.TaxRegistrationNumber))
        {
            b.Line($"Tax Reg: {config.TaxRegistrationNumber}");
        }

        b.Rule('=');

        // ── Meta ─────────────────────────────────────────────────────────────────────────────
        b.AlignLeft()
         .TwoColumn("Invoice", invoice.InvoiceNumber);

        b.TwoColumn("Date", invoice.CreatedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(cashierName))
        {
            b.TwoColumn("Cashier", cashierName);
        }

        if (!string.IsNullOrWhiteSpace(shiftNumber))
        {
            b.TwoColumn("Shift", shiftNumber);
        }

        b.Rule('-');

        // ── Items ────────────────────────────────────────────────────────────────────────────
        // ThreeColumn adapts to 32 or 48 columns, so the header never wraps on 58 mm paper.
        b.Bold().ThreeColumn("ITEM", "QTY", "AMOUNT").Bold(false);
        b.Rule('-');

        foreach (var item in invoice.Items)
        {
            var quantity = item.Quantity.ToString("0.##", CultureInfo.InvariantCulture);
            var amount = EscPosBuilder.Money(item.TotalPrice + item.TaxAmount);

            // Long product names fold onto their own lines; the money column stays aligned.
            foreach (var nameLine in EscPosBuilder.WrapLines(item.ProductName, config.ReceiptColumns - 2))
            {
                b.Line($" {nameLine}");
            }

            b.AlignRight()
             .Line($"{quantity} x {EscPosBuilder.Money(item.UnitPrice)}   {amount}")
             .AlignLeft();

            if (item.DiscountAmount > 0)
            {
                b.AlignRight().Line($"  Item discount -{EscPosBuilder.Money(item.DiscountAmount)}").AlignLeft();
            }
        }

        b.Rule('-');

        // ── Totals ───────────────────────────────────────────────────────────────────────────
        b.TwoColumn("Subtotal", Format(invoice.Subtotal));

        if (invoice.DiscountAmount > 0)
        {
            b.TwoColumn("Discount", "-" + Format(invoice.DiscountAmount));
        }

        if (!string.IsNullOrWhiteSpace(invoice.TaxBreakdownJson))
        {
            foreach (var band in ExtractTaxBands(invoice))
            {
                b.TwoColumn($"Tax {band.Label}", Format(band.TaxAmount));
            }
        }
        else if (invoice.TaxAmount > 0)
        {
            b.TwoColumn("Tax", Format(invoice.TaxAmount));
        }

        b.Bold()
         .Size(1, 2)
         .TwoColumn("TOTAL", $"{Format(invoice.TotalAmount)} {Currency}")
         .Size(1, 1)
         .Bold(false);

        b.Rule('=');

        // ── Tender ───────────────────────────────────────────────────────────────────────────
        b.TwoColumn("Paid by", invoice.PaymentMethod.ToString().ToUpperInvariant());

        if (invoice.AmountTendered > 0)
        {
            b.TwoColumn("Tendered", Format(invoice.AmountTendered));
        }

        if (invoice.ChangeGiven > 0)
        {
            b.Bold().TwoColumn("CHANGE", Format(invoice.ChangeGiven)).Bold(false);
        }

        // ── Digital receipt ──────────────────────────────────────────────────────────────────
        if (config.PrintQrCode && !string.IsNullOrWhiteSpace(config.DigitalReceiptBaseUrl))
        {
            b.Feed(1).AlignCenter();
            b.QrCode($"{config.DigitalReceiptBaseUrl}{invoice.InvoiceNumber}", moduleSize: config.PaperWidth == PaperWidth.Mm58 ? (byte)4 : (byte)6);
            b.Line("Scan for digital receipt");
            b.AlignLeft();
        }

        if (config.PrintReceiptBarcode)
        {
            b.Feed(1).AlignCenter()
             .Barcode(invoice.InvoiceNumber, EscPosCommand.SymbologyCode128, height: 55)
             .AlignLeft();
        }

        // ── Footer ───────────────────────────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(config.ReceiptFooter))
        {
            b.Feed(1).Rule('-').Wrap(config.ReceiptFooter!, center: true);
        }

        b.AlignCenter()
         .Line(invoice.InvoiceNumber)
         .Line(DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture))
         .AlignLeft()
         .Feed(2);

        if (config.AutoCut)
        {
            b.Cut();
        }

        return b.Build();
    }

    /// <summary>Standalone drawer pulse — used by the manual "Open Drawer" button.</summary>
    public static byte[] BuildDrawerPulse(HardwareConfig config)
    {
        var b = new EscPosBuilder(config.ReceiptColumns, EscPosEncoding.ForIndex(config.CodePage)).Init(config.CodePage);
        var pin = config.DrawerConnectionType == DrawerConnectionType.PrinterKickPin5 ? (byte)1 : (byte)0;

        // Prefer the operator-configured command; fall back to the canonical pin-2 pulse.
        if (string.IsNullOrWhiteSpace(config.OpenDrawerCommand))
        {
            b.OpenDrawer(pin);
        }
        else
        {
            b.OpenDrawer(config.OpenDrawerCommand);
        }

        b.Feed(1);
        return b.Build();
    }

    // ── Z-Report ─────────────────────────────────────────────────────────────────────────────

    public static byte[] BuildZReport(ShiftSummary s, HardwareConfig config)
    {
        var b = new EscPosBuilder(config.ReceiptColumns, EscPosEncoding.ForIndex(config.CodePage)).Init(config.CodePage);

        b.AlignCenter()
         .Bold().Size(1, 2).Line("Z-REPORT").Size(1, 1).Bold(false)
         .Line(config.StoreName)
         .Rule('=');

        b.AlignLeft()
         .TwoColumn("Shift", s.ShiftNumber)
         .TwoColumn("Cashier", s.CashierName)
         .TwoColumn("Opened", s.OpenedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture))
         .TwoColumn("Closed", s.ClosedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture));

        b.Rule('-').Bold().Line("SALES").Bold(false);

        b.TwoColumn("Invoices", s.InvoiceCount.ToString(CultureInfo.InvariantCulture))
         .TwoColumn("Items sold", s.ItemCount.ToString(CultureInfo.InvariantCulture))
         .TwoColumn("Gross sales", Format(s.GrossSales))
         .TwoColumn("Net sales", Format(s.NetSales))
         .TwoColumn("Discounts", Format(s.DiscountTotal))
         .TwoColumn("Refunds", Format(s.RefundTotal))
         .TwoColumn("Tax collected", Format(s.TaxTotal));

        if (s.TaxBands.Count > 0)
        {
            b.Rule('-').Bold().Line("TAX BREAKDOWN").Bold(false);
            foreach (var band in s.TaxBands)
            {
                b.TwoColumn($" {band.Label} net", Format(band.NetAmount))
                 .TwoColumn($" {band.Label} tax", Format(band.TaxAmount));
            }
        }

        b.Rule('-').Bold().Line("PAYMENTS").Bold(false);
        b.TwoColumn("Cash", Format(s.CashTotal))
         .TwoColumn("Card", Format(s.CardTotal));

        if (s.SplitTotal != 0)
        {
            b.TwoColumn("Split", Format(s.SplitTotal));
        }

        b.Rule('-').Bold().Line("CASH RECONCILIATION").Bold(false);
        b.TwoColumn("Opening float", Format(s.OpeningFloat))
         .TwoColumn("Cash sales", Format(s.CashTotal))
         .TwoColumn("Paid in", Format(s.PaidIn))
         .TwoColumn("Paid out", Format(s.PaidOut))
         .TwoColumn("Expected drawer", Format(s.ExpectedCash))
         .TwoColumn("Counted", Format(s.CountedCash));

        b.Bold().TwoColumn("DIFFERENCE", Format(s.CashDifference)).Bold(false);

        b.Rule('=')
         .TwoColumn("Average basket", Format(s.AverageBasket))
         .AlignCenter()
         .Feed(1)
         .Line("Report generated " + DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture))
         .Line("Signature: ______________________")
         .Feed(3);

        if (config.AutoCut)
        {
            b.Cut();
        }

        return b.Build();
    }

    // ── Test page ────────────────────────────────────────────────────────────────────────────

    public static byte[] BuildTestPage(HardwareConfig config)
    {
        var b = new EscPosBuilder(config.ReceiptColumns, EscPosEncoding.ForIndex(config.CodePage)).Init(config.CodePage);

        b.AlignCenter()
         .Bold().Size(2, 1).Line("KACHER POS").Size(1, 1).Bold(false)
         .Line("Hardware test receipt")
         .Rule('=');

        b.AlignLeft()
         .TwoColumn("Transport", config.ConnectionType.ToString())
         .TwoColumn("Target", DescribeTarget(config))
         .TwoColumn("Paper", $"{(int)config.PaperWidth} mm / {config.ReceiptColumns} cols")
         .TwoColumn("Code page", EscPosEncoding.NameFor(config.CodePage))
         .TwoColumn("Cut", config.AutoCut ? "enabled" : "disabled")
         .TwoColumn("Printed", DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture));

        b.Rule('-')
         .Line("Left aligned")
         .AlignCenter().Line("Centred")
         .AlignRight().Line("Right aligned")
         .AlignLeft()
         .Bold().Line("Bold").Bold(false)
         .Underline().Line("Underlined").Underline(false)
         .Reverse().Line(" Reverse video ").Reverse(false)
         .Size(2, 2).Line("Large").Size(1, 1);

        b.Rule('-').Line("Arabic / accents: ").Line("Café · naïve · 15% · EGP");

        if (config.PrintReceiptBarcode)
        {
            b.AlignCenter().Barcode("KACHER-TEST-0001", height: 50).AlignLeft();
        }

        if (config.PrintQrCode)
        {
            b.AlignCenter().QrCode("https://kacher.local/hw-test", moduleSize: 6).Line("QR self-test").AlignLeft();
        }

        b.Rule('=')
         .AlignCenter()
         .Wrap("If this receipt is crisp and unclipped, the printer profile is correct.", center: true)
         .Feed(3);

        if (config.AutoCut)
        {
            b.Cut();
        }

        return b.Build();
    }

    public static string DescribeTarget(HardwareConfig config) => config.ConnectionType switch
    {
        PrinterConnectionType.Network => $"{config.NetworkHost}:{config.NetworkPort}",
        PrinterConnectionType.Serial => $"{config.SerialPortName} @ {config.BaudRate}",
        PrinterConnectionType.Usb => config.UsbVendorProductId ?? "USB auto-detect",
        _ => config.ThermalPrinterName
    };

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static string Format(decimal value) => EscPosBuilder.Money(value) + " " + Currency;

    /// <summary>Parses the persisted tax snapshot into printable bands.</summary>
    private static IReadOnlyList<TaxBand> ExtractTaxBands(Invoice invoice)
    {
        var bands = invoice.Items
            .GroupBy(i => i.TaxRate)
            .Select(g => new TaxBand(g.Key, g.Sum(x => x.TotalPrice), g.Sum(x => x.TaxAmount)))
            .Where(b => b.TaxAmount != 0)
            .OrderByDescending(b => b.Rate)
            .ToList();

        return bands.Count > 0 ? bands : Array.Empty<TaxBand>();
    }
}
