using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;
using Kacher.Pos.Services.Hardware.Scanner;
using Kacher.Pos.Services.Repositories;

namespace Kacher.Pos.Services.Hardware;

/// <summary>Immutable snapshot bound to the top status bar.</summary>
public sealed record HardwareSnapshot(
    HardwareHealth Printer,
    string PrinterDetail,
    HardwareHealth Scanner,
    string ScannerDetail,
    HardwareHealth Drawer,
    string DrawerDetail,
    DateTime CheckedAt)
{
    public static HardwareSnapshot Unknown { get; } = new(
        HardwareHealth.Unknown, "Not probed",
        HardwareHealth.Unknown, "Not probed",
        HardwareHealth.Unknown, "Not probed",
        DateTime.MinValue);
}

public interface IHardwareMonitor : IDisposable
{
    HardwareSnapshot Current { get; }

    event EventHandler<HardwareSnapshot>? StatusChanged;

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync();

    Task<HardwareSnapshot> RefreshAsync(CancellationToken ct = default);
}

/// <summary>
/// Background health poller feeding the printer / scanner / drawer pills.
///
/// Design points:
/// * <see cref="PeriodicTimer"/> drives the loop — no DispatcherTimer, so the UI thread is untouched.
/// * Probing a Windows queue is instantaneous; a network probe is capped at 1.5 s inside the
///   transport, so a powered-off printer never stalls the loop.
/// * Results are only raised when something changed, preventing needless UI churn at 60 fps.
/// </summary>
public sealed class HardwareMonitorService : IHardwareMonitor
{
    private readonly IReceiptPrinter _printer;
    private readonly IBarcodeScannerService _scanner;
    private readonly IHardwareConfigRepository _configs;
    private readonly IAppLog _log;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private HardwareSnapshot _current = HardwareSnapshot.Unknown;

    public HardwareMonitorService(
        IReceiptPrinter printer,
        IBarcodeScannerService scanner,
        IHardwareConfigRepository configs,
        IAppLog log)
    {
        _printer = printer;
        _scanner = scanner;
        _configs = configs;
        _log = log;
    }

    public HardwareSnapshot Current => _current;

    public event EventHandler<HardwareSnapshot>? StatusChanged;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_loop is not null)
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => PollLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // Give the shell a moment to become visible before the first probe.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
            do
            {
                await RefreshAsync(ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _log.Error("Hardware monitor loop stopped unexpectedly.", ex);
        }
    }

    public async Task<HardwareSnapshot> RefreshAsync(CancellationToken ct = default)
    {
        var config = await _configs.GetActiveAsync(ct).ConfigureAwait(false);
        var probe = await _printer.ProbeAsync(config, ct).ConfigureAwait(false);

        var printerHealth = probe.IsAvailable ? HardwareHealth.Online : HardwareHealth.Offline;

        // Drawer shares the printer's link: a printer that is unreachable cannot pulse the kick pin.
        var drawerHealth = config.DrawerConnectionType == DrawerConnectionType.SerialPort
            ? HardwareHealth.Unknown
            : printerHealth;

        var scannerHealth = _scanner.Health;

        var snapshot = new HardwareSnapshot(
            printerHealth,
            probe.Message,
            scannerHealth,
            _scanner.StatusMessage,
            drawerHealth,
            drawerHealth == HardwareHealth.Online ? "Kick pin 2 ready" : "No drawer link",
            DateTime.Now);

        var changed = snapshot with { CheckedAt = _current.CheckedAt } != _current;
        _current = snapshot;

        if (changed)
        {
            StatusChanged?.Invoke(this, snapshot);
        }

        return snapshot;
    }

    public async Task StopAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _loop = null;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
