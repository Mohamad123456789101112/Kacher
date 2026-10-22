using System.Diagnostics;
using System.IO;
using Kacher.Pos.Data;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Kacher.Pos.Services.Hardware.Transports;
using Kacher.Pos.Services.Printing;

namespace Kacher.Pos.Services.Hardware;

public sealed record PrintResult(bool Success, string Message, int Bytes, TimeSpan Duration)
{
    public static PrintResult Fail(string message) => new(false, message, 0, TimeSpan.Zero);
}

/// <summary>
/// Native ESC/POS engine. Everything the till needs to talk to a thermal printer, a cash drawer and
/// a barcode scanner lives behind this interface.
/// </summary>
public interface IReceiptPrinter
{
    Task<PrintResult> PrintSaleReceiptAsync(
        Invoice invoice, HardwareConfig config, User? cashier, Shift? shift, CancellationToken ct = default);

    Task<PrintResult> PrintZReportAsync(ShiftSummary summary, HardwareConfig config, CancellationToken ct = default);

    Task<PrintResult> PrintTestAsync(HardwareConfig config, CancellationToken ct = default);

    Task<PrintResult> OpenDrawerAsync(HardwareConfig config, CancellationToken ct = default);

    Task<TransportProbe> ProbeAsync(HardwareConfig config, CancellationToken ct = default);
}

/// <summary>
/// Sends pre-built ESC/POS streams to the configured device.
///
/// Concurrency: a <see cref="SemaphoreSlim"/> serialises jobs. Thermal printers have a small input
/// buffer (~4 KB); interleaving two jobs corrupts both. Serialising also gives a natural place to
/// apply the retry policy for flaky Wi-Fi printers.
/// </summary>
public sealed class EpsonEscPosPrinterService : IReceiptPrinter, IDisposable
{
    private readonly IReceiptTransportFactory _transportFactory;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _jobLock = new(1, 1);

    public EpsonEscPosPrinterService(IReceiptTransportFactory transportFactory, IAppLog log)
    {
        _transportFactory = transportFactory;
        _log = log;
    }

    public Task<PrintResult> PrintSaleReceiptAsync(
        Invoice invoice, HardwareConfig config, User? cashier, Shift? shift, CancellationToken ct = default)
    {
        var logo = config.PrintLogo
            ? LogoRasterizer.TryRasterize(ResolveLogoPath(), config.PaperWidth == PaperWidth.Mm58 ? 288 : 384)
            : null;

        var payload = ReceiptDocumentBuilder.BuildSaleReceipt(
            invoice,
            config,
            cashier?.DisplayName,
            shift?.ShiftNumber,
            logo?.Data,
            logo?.WidthBytes ?? 0,
            logo?.HeightDots ?? 0);

        return SendAsync(
            payload,
            config,
            $"Receipt {invoice.InvoiceNumber}",
            pulseDrawerAfter: DrawerPolicy.ShouldOpen(invoice, config),
            allowCopies: true,
            ct: ct);
    }

    public Task<PrintResult> PrintZReportAsync(ShiftSummary summary, HardwareConfig config, CancellationToken ct = default)
    {
        var payload = ReceiptDocumentBuilder.BuildZReport(summary, config);
        return SendAsync(payload, config, $"Z-Report {summary.ShiftNumber}",
            pulseDrawerAfter: false, allowCopies: false, ct: ct);
    }

    public Task<PrintResult> PrintTestAsync(HardwareConfig config, CancellationToken ct = default)
    {
        var payload = ReceiptDocumentBuilder.BuildTestPage(config);
        return SendAsync(payload, config, "Hardware test",
            pulseDrawerAfter: false, allowCopies: false, ct: ct);
    }

    public Task<PrintResult> OpenDrawerAsync(HardwareConfig config, CancellationToken ct = default)
    {
        // The payload *is* the pulse: never pulse twice and never repeat it for "copies".
        var payload = ReceiptDocumentBuilder.BuildDrawerPulse(config);
        return SendAsync(payload, config, "Drawer pulse",
            pulseDrawerAfter: false, allowCopies: false, ct: ct);
    }

    public Task<TransportProbe> ProbeAsync(HardwareConfig config, CancellationToken ct = default)
    {
        // Probing must never queue behind a print job, and must never throw into the status bar.
        return Task.Run(async () =>
        {
            try
            {
                await using var transport = _transportFactory.Create(config);
                return await transport.ProbeAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return TransportProbe.Offline(ex.Message);
            }
        }, ct);
    }

    private async Task<PrintResult> SendAsync(
        byte[] payload,
        HardwareConfig config,
        string jobName,
        bool pulseDrawerAfter,
        bool allowCopies,
        CancellationToken ct)
    {
        if (payload.Length == 0)
        {
            return PrintResult.Fail("Nothing to print.");
        }

        await _jobLock.WaitAsync(ct).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var copies = allowCopies ? Math.Clamp(config.ReceiptCopies, 1, 3) : 1;

            for (var copy = 1; copy <= copies; copy++)
            {
                // Network printers are the only transport that fails transiently in practice
                // (Wi-Fi roam, DHCP lease), so a single retry is enough and keeps latency bounded.
                var attempt = 0;
                while (true)
                {
                    attempt++;
                    try
                    {
                        await using var transport = _transportFactory.Create(config);
                        await transport.SendAsync(payload, ct).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception ex) when (attempt < 2 && config.ConnectionType == PrinterConnectionType.Network)
                    {
                        _log.Warn($"{jobName}: network print attempt {attempt} failed ({ex.Message}); retrying once.");
                        await Task.Delay(250, ct).ConfigureAwait(false);
                    }
                }
            }

            // Drawer pulse is a separate short job on the same connection so the receipt is never
            // delayed by a retry of the kick command.
            if (pulseDrawerAfter && config.OpenDrawerOnSale && config.DrawerConnectionType != DrawerConnectionType.SerialPort)
            {
                var drawerPin = config.DrawerConnectionType == DrawerConnectionType.PrinterKickPin5 ? (byte)1 : (byte)0;
                var pulse = ReceiptDocumentBuilder.BuildDrawerPulse(config);
                await using var transport = _transportFactory.Create(config);
                await transport.SendAsync(pulse, ct).ConfigureAwait(false);
                _log.Info($"Drawer pulse sent (pin {drawerPin}).");
            }

            stopwatch.Stop();
            _log.Info($"{jobName} printed via {config.ConnectionType} in {stopwatch.ElapsedMilliseconds} ms ({payload.Length} bytes).");

            return new PrintResult(true, $"{jobName} sent to printer.", payload.Length, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return PrintResult.Fail("Printing was cancelled.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _log.Error($"{jobName} failed: {ex.Message}", ex);
            return PrintResult.Fail(ex.Message);
        }
        finally
        {
            _jobLock.Release();
        }
    }

    private static string? ResolveLogoPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "kacher.png");
        return File.Exists(path) ? path : null;
    }

    public void Dispose() => _jobLock.Dispose();
}

/// <summary>
/// When the sale should kick the drawer:
///   cash sale            → always (configurable)
///   card / split sale    → only when configured, since there is no cash to deposit
///   manual button        → always
/// </summary>
public static class DrawerPolicy
{
    public static bool ShouldOpen(Invoice invoice, HardwareConfig config)
    {
        if (!config.OpenDrawerOnSale)
        {
            return false;
        }

        if (!config.OpenDrawerOnCashPayment)
        {
            return true;
        }

        return invoice.Payments.Any(p => p.Method is PaymentMethod.Cash or PaymentMethod.Split)
               || invoice.PaymentMethod is PaymentMethod.Cash or PaymentMethod.Split;
    }
}

/// <summary>PDF / file fallback when no printer is configured, and the export target for reports.</summary>
public interface IReceiptArchive
{
    Task<string> SaveReceiptTextAsync(Invoice invoice, HardwareConfig config, CancellationToken ct = default);
}

public sealed class ReceiptArchive : IReceiptArchive
{
    public async Task<string> SaveReceiptTextAsync(Invoice invoice, HardwareConfig config, CancellationToken ct = default)
    {
        var path = Path.Combine(DatabaseConnectionFactory.ReportsDirectory, $"{invoice.InvoiceNumber}.txt");
        var lines = new List<string>
        {
            config.StoreName,
            config.StoreAddress ?? string.Empty,
            new string('=', config.ReceiptColumns),
            $"Invoice : {invoice.InvoiceNumber}",
            $"Date    : {invoice.CreatedAtUtc:dd/MM/yyyy HH:mm}",
            new string('-', config.ReceiptColumns)
        };

        lines.AddRange(invoice.Items.Select(i =>
            $"{i.ProductName,-24} {i.Quantity,4:0.##} x {i.UnitPrice,7:0.00} {i.TotalPrice,8:0.00}"));

        lines.Add(new string('-', config.ReceiptColumns));
        lines.Add($"Subtotal {invoice.Subtotal:0.00}   Tax {invoice.TaxAmount:0.00}   TOTAL {invoice.TotalAmount:0.00}");
        lines.Add(config.ReceiptFooter ?? string.Empty);

        await File.WriteAllLinesAsync(path, lines, ct).ConfigureAwait(false);
        return path;
    }
}
