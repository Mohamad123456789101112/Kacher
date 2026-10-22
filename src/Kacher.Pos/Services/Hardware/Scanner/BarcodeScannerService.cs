using System.IO.Ports;
using System.Text;
using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;

namespace Kacher.Pos.Services.Hardware.Scanner;

public sealed class BarcodeScannedEventArgs : EventArgs
{
    public BarcodeScannedEventArgs(string barcode, BarcodeSource source)
    {
        Barcode = barcode;
        Source = source;
    }

    public string Barcode { get; }
    public BarcodeSource Source { get; }
}

public enum BarcodeSource
{
    KeyboardWedge,
    SerialPort
}

public interface IBarcodeScannerService : IDisposable
{
    event EventHandler<BarcodeScannedEventArgs>? BarcodeScanned;

    /// <summary>True while the serial reader is attached, or when a wedge burst was seen recently.</summary>
    bool IsHealthy { get; }

    HardwareHealth Health { get; }

    string StatusMessage { get; }

    /// <summary>
    /// Feed one character from the window's PreviewKeyDown. The service joins characters into a
    /// barcode using inter-key timing — no focus stealing, no TextBox required.
    /// </summary>
    void FeedKey(char character);

    /// <summary>Feed an explicit terminator (Enter / Tab). Emits immediately.</summary>
    void FeedTerminator();

    Task StartAsync(HardwareConfig config, CancellationToken ct = default);

    Task StopAsync();
}

/// <summary>
/// Barcode capture for both transport styles.
///
/// KEYBOARD WEDGE (the common case): the scanner presents itself as a USB HID keyboard and types
/// the barcode in a few milliseconds followed by Enter. We watch the window's key stream and rely on
/// *timing*, not focus: a burst of printable characters separated by &lt; 60 ms each is a scan, while
/// a human typing at 200 ms per key is not. This is why the search box keeps focus — the scan never
/// needs to be routed there.
///
/// SERIAL: a COM-port scanner streams ASCII terminated by CR/LF; we read asynchronously with
/// <see cref="SerialPort.BaseStream"/> and decode in a background loop.
/// </summary>
public sealed class BarcodeScannerService : IBarcodeScannerService
{
    private readonly IAppLog _log;
    private readonly object _bufferGate = new();

    private readonly StringBuilder _buffer = new(64);
    private DateTime _lastKeystroke = DateTime.MinValue;
    private DateTime _lastSerialScan = DateTime.MinValue;
    private DateTime _lastWedgeScan = DateTime.MinValue;

    private HardwareConfig? _config;
    private SerialPort? _serial;
    private CancellationTokenSource? _serialCts;
    private Task? _serialLoop;

    public BarcodeScannerService(IAppLog log) => _log = log;

    public event EventHandler<BarcodeScannedEventArgs>? BarcodeScanned;

    public HardwareHealth Health
    {
        get
        {
            if (_serial?.IsOpen == true)
            {
                return HardwareHealth.Online;
            }

            // A wedge scanner is "online" if we have seen a burst in the last 10 minutes, or if the
            // operator configured one without a COM port (i.e. they expect HID behaviour).
            var wedgeRecentlySeen = (DateTime.UtcNow - _lastWedgeScan).TotalMinutes < 10;
            var serialRecentlySeen = (DateTime.UtcNow - _lastSerialScan).TotalMinutes < 10;

            return wedgeRecentlySeen || serialRecentlySeen
                   || string.IsNullOrWhiteSpace(_config?.BarcodeScannerPort)
                ? HardwareHealth.Online
                : HardwareHealth.Unknown;
        }
    }

    public bool IsHealthy => Health == HardwareHealth.Online;

    public string StatusMessage => _serial?.IsOpen == true
        ? $"Serial scanner on {_config?.BarcodeScannerPort}"
        : "Keyboard-wedge scanner";

    // ── Keyboard wedge ───────────────────────────────────────────────────────────────────────

    public void FeedKey(char character)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(_config?.ScannerInterKeyTimeoutMs ?? 60, 20, 200));
        var now = DateTime.UtcNow;

        lock (_bufferGate)
        {
            // Gap too large → this starts a new barcode; discard any partial buffer.
            if (now - _lastKeystroke > timeout)
            {
                _buffer.Clear();
            }

            _lastKeystroke = now;

            if (char.IsControl(character))
            {
                return;
            }

            _buffer.Append(character);

            // Safety valve: a stuck key must not grow the buffer without bound.
            if (_buffer.Length > 128)
            {
                _buffer.Clear();
            }
        }
    }

    public void FeedTerminator()
    {
        string? barcode;
        lock (_bufferGate)
        {
            barcode = TakeBuffer();
        }

        Emit(barcode, BarcodeSource.KeyboardWedge);
    }

    /// <summary>
    /// Called by the shell on a short timer (or after each key) so a scanner configured without a
    /// trailing Enter still resolves. Cheap: one timestamp comparison when the buffer is empty.
    /// </summary>
    public void FlushIfIdle()
    {
        string? barcode;
        lock (_bufferGate)
        {
            if (_buffer.Length == 0)
            {
                return;
            }

            var timeout = TimeSpan.FromMilliseconds(Math.Clamp(_config?.ScannerInterKeyTimeoutMs ?? 60, 20, 200));
            if (DateTime.UtcNow - _lastKeystroke < timeout)
            {
                return;
            }

            barcode = TakeBuffer();
        }

        Emit(barcode, BarcodeSource.KeyboardWedge);
    }

    private string? TakeBuffer()
    {
        var value = _buffer.ToString().Trim();
        _buffer.Clear();
        var minLength = Math.Clamp(_config?.ScannerMinLength ?? 3, 1, 20);
        return value.Length >= minLength ? value : null;
    }

    private void Emit(string? barcode, BarcodeSource source)
    {
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return;
        }

        if (source == BarcodeSource.KeyboardWedge)
        {
            _lastWedgeScan = DateTime.UtcNow;
        }
        else
        {
            _lastSerialScan = DateTime.UtcNow;
        }

        _log.Debug($"Barcode scanned ({source}): {barcode}");
        BarcodeScanned?.Invoke(this, new BarcodeScannedEventArgs(barcode, source));
    }

    // ── Serial scanner ──────────────────────────────────────────────────────────────────────

    public Task StartAsync(HardwareConfig config, CancellationToken ct = default)
    {
        _config = config;

        if (string.IsNullOrWhiteSpace(config.BarcodeScannerPort))
        {
            _log.Info("Barcode scanner: keyboard-wedge mode.");
            return Task.CompletedTask;
        }

        try
        {
            _serial = new SerialPort(
                config.BarcodeScannerPort,
                config.BarcodeScannerBaudRate <= 0 ? 9600 : config.BarcodeScannerBaudRate,
                Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 1000,
                WriteTimeout = 1000,
                DtrEnable = true,
                RtsEnable = true
            };

            _serial.Open();
            _serialCts = new CancellationTokenSource();
            _serialLoop = Task.Run(() => ReadSerialAsync(_serial, _serialCts.Token), _serialCts.Token);

            _log.Info($"Barcode scanner: serial mode on {config.BarcodeScannerPort}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warn($"Serial scanner unavailable on {config.BarcodeScannerPort}: {ex.Message}. Falling back to keyboard wedge.");
            _serial = null;
        }

        return Task.CompletedTask;
    }

    private async Task ReadSerialAsync(SerialPort port, CancellationToken ct)
    {
        var line = new StringBuilder(64);
        var buffer = new byte[256];

        try
        {
            var stream = port.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                    continue;
                }

                for (var i = 0; i < read; i++)
                {
                    var ch = (char)buffer[i];
                    if (ch is '\r' or '\n')
                    {
                        Emit(line.ToString().Trim(), BarcodeSource.SerialPort);
                        line.Clear();
                    }
                    else if (!char.IsControl(ch))
                    {
                        line.Append(ch);
                        if (line.Length > 128)
                        {
                            line.Clear();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            _log.Error("Serial scanner read loop stopped.", ex);
        }
    }

    public async Task StopAsync()
    {
        try
        {
            _serialCts?.Cancel();
            if (_serialLoop is not null)
            {
                await _serialLoop.ConfigureAwait(false);
            }

            if (_serial is { IsOpen: true })
            {
                _serial.Close();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Error closing scanner port: {ex.Message}");
        }
        finally
        {
            _serial?.Dispose();
            _serial = null;
            _serialCts?.Dispose();
            _serialCts = null;
            _serialLoop = null;
        }
    }

    public void Dispose()
    {
        try
        {
            _serialCts?.Cancel();
            if (_serial is { IsOpen: true })
            {
                _serial.Close();
            }
            _serial?.Dispose();
        }
        catch (Exception)
        {
            // shutdown only
        }
        finally
        {
            _serialCts?.Dispose();
        }
    }
}
