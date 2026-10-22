using System.IO.Ports;

namespace Kacher.Pos.Services.Hardware.Transports;

/// <summary>
/// RS-232 / USB-serial printer transport. Kept for legacy TM-T88 units and for kiosk builds where
/// the printer is daisy-chained with the barcode scanner.
///
/// <see cref="SerialPort"/> has no true async surface on Windows, so the (blocking) base-stream
/// write is wrapped in <see cref="Task.Run(Func{Task})"/>. That keeps the UI thread free — the
/// requirement — while acknowledging that the I/O itself is a kernel call on a thread-pool thread.
/// </summary>
public sealed class SerialEscPosTransport : IReceiptTransport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private SerialPort? _port;

    public SerialEscPosTransport(string portName, int baudRate = 9600)
    {
        _portName = portName;
        _baudRate = baudRate <= 0 ? 9600 : baudRate;
    }

    public string Description => $"Serial {_portName} @ {_baudRate}";

    public bool IsConnected => _port?.IsOpen == true;

    private SerialPort EnsureOpen()
    {
        if (_port is { IsOpen: true })
        {
            return _port;
        }

        var port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            WriteTimeout = 5000,
            ReadTimeout = 1500
        };

        port.Open();
        _port = port;
        return port;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var port = EnsureOpen();
        var buffer = payload.ToArray();

        await Task.Run(async () =>
        {
            var stream = port.BaseStream;
            await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public Task<TransportProbe> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var port = EnsureOpen();

            // DSR/DCD high generally means the printer is powered and ready on RS-232.
            var ready = port.DsrHolding || port.CDHolding;
            return Task.FromResult(ready
                ? TransportProbe.Online($"{_portName} ready", TimeSpan.Zero)
                : TransportProbe.Online($"{_portName} open (no DSR)", TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult(TransportProbe.Offline(ex.Message));
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_port is { IsOpen: true })
            {
                _port.Close();
            }
        }
        catch (IOException)
        {
            // port already gone
        }

        _port?.Dispose();
        _port = null;
        return ValueTask.CompletedTask;
    }
}
