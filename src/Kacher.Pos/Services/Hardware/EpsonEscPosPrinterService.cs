using System.Diagnostics;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Kacher.Pos.Services.Hardware.EscPos;
using Kacher.Pos.Services.Hardware.Transports;
using Kacher.Pos.Services.Printing;

namespace Kacher.Pos.Services.Hardware;

/// <summary>Outcome of one print job. The message is written for the cashier, not the developer.</summary>
public sealed record PrintResult(bool Success, string Message, int Bytes, TimeSpan Duration)
{
    public static PrintResult Failed(string message) => new(false, message, 0, TimeSpan.Zero);
}

/// <summary>Result of a reachability check, surfaced by the status bar and the settings screen.</summary>
public sealed record ProbeResult(bool IsAvailable, string Message);

public interface IReceiptPrinter
{
    /// <summary>
    /// Prints the customer receipt for a committed sale, including the drawer kick when the
    /// configured policy allows it. Never throws for hardware reasons — the sale is already saved.
    /// </summary>
    Task<PrintResult> PrintSaleReceiptAsync(
        Invoice invoice,
        HardwareConfig config,
        User? cashier,
        Shift? shift,
        CancellationToken ct = default);

    Task<PrintResult> PrintZReportAsync(ShiftSummary summary, HardwareConfig config, CancellationToken ct = default);

    Task<PrintResult> PrintTestAsync(HardwareConfig config, CancellationToken ct = default);

    /// <summary>Pulses the drawer without printing. Used by the manual "open drawer" button.</summary>
    Task<PrintResult> OpenDrawerAsync(HardwareConfig config, CancellationToken ct = default);

    /// <summary>Cheap reachability probe; does not print anything.</summary>
    Task<ProbeResult> ProbeAsync(HardwareConfig config, CancellationToken ct = default);
}

/// <summary>
/// Epson-compatible ESC/POS printing service: builds the byte stream, walks the transport
/// candidates, sends the job and reports a result the UI can display without throwing.
/// </summary>
/// <remarks>
/// <para>
/// Jobs are serialised on a single gate. Two cashiers pressing "print" at the same moment would
/// otherwise interleave bytes on a shared spooler queue and produce one corrupted receipt instead
/// of two correct ones. The gate is held only for the duration of the IO — never while the document
/// is being built — so the Dispatcher stays responsive.
/// </para>
/// <para>
/// Printing is deliberately *after* the sale is committed and deliberately non-fatal: if every
/// printer path fails the job still lands in the file spool and the cashier is told where it is.
/// </para>
/// </remarks>
public sealed class EpsonEscPosPrinterService : IReceiptPrinter, IDisposable
{
    private readonly IReceiptTransportFactory _transports;
    private readonly IAppLog _log;
    private readonly SemaphoreSlim _jobGate = new(1, 1);
    private bool _disposed;

    public EpsonEscPosPrinterService(IReceiptTransportFactory transports, IAppLog log)
    {
        _transports = transports;
        _log = log;
    }

    public async Task<PrintResult> PrintSaleReceiptAsync(
        Invoice invoice,
        HardwareConfig config,
        User? cashier,
        Shift? shift,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(config);

        byte[] payload;

        try
        {
            var openDrawer = ShouldOpenDrawer(invoice, config);
            payload = ReceiptDocumentBuilder.BuildSaleReceipt(invoice, config, cashier?.DisplayName, shift, openDrawer);
        }
        catch (Exception ex)
        {
            _log.Error("Could not render the receipt.", ex);
            return PrintResult.Failed("The receipt could not be rendered.");
        }

        var copies = Math.Clamp(config.ReceiptCopies, 1, 3);
        return await SendAsync(payload, config, copies, $"receipt {invoice.InvoiceNumber}", ct).ConfigureAwait(false);
    }

    public async Task<PrintResult> PrintZReportAsync(ShiftSummary summary, HardwareConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(config);

        byte[] payload;

        try
        {
            payload = ReceiptDocumentBuilder.BuildZReport(summary, config);
        }
        catch (Exception ex)
        {
            _log.Error("Could not render the Z-report.", ex);
            return PrintResult.Failed("The Z-report could not be rendered.");
        }

        return await SendAsync(payload, config, 1, $"Z-report {summary.ShiftNumber}", ct).ConfigureAwait(false);
    }

    public async Task<PrintResult> PrintTestAsync(HardwareConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var payload = ReceiptDocumentBuilder.BuildTestPage(config, DateTime.Now);
        return await SendAsync(payload, config, 1, "test page", ct).ConfigureAwait(false);
    }

    public async Task<PrintResult> OpenDrawerAsync(HardwareConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.DrawerConnectionType == DrawerConnectionType.SerialPort)
        {
            // A drawer wired to its own COM port is not driven by the printer's kick line.
            _log.Warn("Drawer is wired to a standalone COM port; the printer kick cannot open it.");
            return PrintResult.Failed("This drawer is wired to a COM port, not the printer's kick pin.");
        }

        var pulse = DrawerCommandParser.ParseOrDefault(config.OpenDrawerCommand);

        // A bare kick is its own job: appending it to a receipt would fire the solenoid twice when
        // the sale policy already added one.
        return await SendAsync(pulse.ToBytes(), config, 1, "drawer kick", ct).ConfigureAwait(false);
    }

    public async Task<ProbeResult> ProbeAsync(HardwareConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        IReadOnlyList<IReceiptTransport> candidates;

        try
        {
            candidates = _transports.CreateFallbacks(config);
        }
        catch (TransportException ex)
        {
            return new ProbeResult(false, ex.Message);
        }

        string lastError = "No transport configured.";

        foreach (var transport in candidates)
        {
            try
            {
                var probe = await transport.ProbeAsync(ct).ConfigureAwait(false);

                if (probe.Reachable)
                {
                    return new ProbeResult(true, probe.Detail);
                }

                lastError = probe.Detail;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        return new ProbeResult(false, lastError);
    }

    // ── Job orchestration ────────────────────────────────────────────────────────────────────

    /// <summary>Drawer policy: sale-level kick, optionally restricted to cash-bearing payments.</summary>
    private static bool ShouldOpenDrawer(Invoice invoice, HardwareConfig config)
    {
        if (!config.OpenDrawerOnSale)
        {
            return false;
        }

        if (!config.OpenDrawerOnCashPayment)
        {
            return true;
        }

        // Split payments always involve cash in practice; a card-only sale must not open the till.
        return invoice.PaymentMethod is PaymentMethod.Cash or PaymentMethod.Split
               || invoice.Payments.Any(p => p.Method == PaymentMethod.Cash);
    }

    private async Task<PrintResult> SendAsync(
        byte[] payload,
        HardwareConfig config,
        int copies,
        string description,
        CancellationToken ct)
    {
        if (_disposed)
        {
            return PrintResult.Failed("The printing service has been shut down.");
        }

        var job = copies <= 1 ? payload : Repeat(payload, copies);
        var stopwatch = Stopwatch.StartNew();

        await _jobGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            IReadOnlyList<IReceiptTransport> candidates;

            try
            {
                candidates = _transports.CreateFallbacks(config);
            }
            catch (TransportException ex)
            {
                // Misconfiguration, not a hardware fault: still capture the document.
                _log.Warn($"Printer profile is incomplete: {ex.Message}");
                candidates = [new FileSpoolTransport(_log)];
            }

            for (var index = 0; index < candidates.Count; index++)
            {
                var transport = candidates[index];

                try
                {
                    await SendWithRetryAsync(transport, job, ct).ConfigureAwait(false);
                    stopwatch.Stop();

                    _log.Info($"Printed {description} ({job.Length} bytes) via {transport.Description}.");

                    var message = index == 0
                        ? $"Sent to {transport.Description}."
                        : $"Primary printer unavailable — printed via {transport.Description}.";

                    return new PrintResult(true, message, job.Length, stopwatch.Elapsed);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warn($"Transport failed for {description} ({transport.Description}): {ex.Message}");
                }
            }

            stopwatch.Stop();
            return new PrintResult(false, "No printer responded — the job was not delivered.", 0, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            _log.Warn($"Print job cancelled: {description}.");
            return PrintResult.Failed("Printing was cancelled.");
        }
        finally
        {
            _jobGate.Release();
        }
    }

    /// <summary>
    /// Sends one job, retrying once for network transports only. A Wi-Fi roam or a DHCP renewal can
    /// drop a single TCP connect; a USB/spooler failure is almost always a real fault (offline,
    /// out of paper) where an immediate second attempt just wastes time.
    /// </summary>
    private static async Task SendWithRetryAsync(IReceiptTransport transport, byte[] payload, CancellationToken ct)
    {
        try
        {
            await transport.SendAsync(payload, ct).ConfigureAwait(false);
        }
        catch (Exception) when (transport is NetworkEscPosTransport && !ct.IsCancellationRequested)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            await transport.SendAsync(payload, ct).ConfigureAwait(false);
        }
    }

    private static byte[] Repeat(byte[] payload, int copies)
    {
        var buffer = new byte[payload.Length * copies];

        for (var i = 0; i < copies; i++)
        {
            Buffer.BlockCopy(payload, 0, buffer, i * payload.Length, payload.Length);
        }

        return buffer;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _jobGate.Dispose();
    }
}
