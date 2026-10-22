using System.IO.Ports;
using Kacher.Pos.Infrastructure;

namespace Kacher.Pos.Services.Hardware.Transports;

/// <summary>
/// Drives a serial (RS-232) thermal printer on a COM port.
/// </summary>
/// <remarks>
/// <para>
/// Serial printers are still common on legacy tills and in kiosks. ESC/POS over RS-232 is
/// byte-for-byte identical to the network path; only framing differs — 8 data bits, no parity,
/// one stop bit and an optional RTS/CTS handshake.
/// </para>
/// <para>
/// <c>SerialPort</c> has no true async API, so the blocking write runs on the thread pool. The port
/// is opened and closed per job: leaving it open would block other applications (and the barcode
/// scanner often shares the same USB-serial chipset family).
/// </para>
/// </remarks>
public sealed class SerialEscPosTransport : IReceiptTransport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly IAppLog _log;

    public SerialEscPosTransport(string portName, int baudRate, IAppLog log)
    {
        _portName = portName;
        _baudRate = baudRate <= 0 ? 9600 : baudRate;
        _log = log;
    }

    public string Description => $"Serial ESC/POS → {_portName} @ {_baudRate} baud";

    public Task SendAsync(byte[] payload, CancellationToken ct = default)
        => Task.Run(() => SendBlocking(payload, ct), CancellationToken.None);

    public Task<TransportProbe> ProbeAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                using var port = CreatePort();
                port.Open();
                port.Close();
                return TransportProbe.Ok($"{_portName} is available.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return TransportProbe.Fail($"Cannot open {_portName}: {ex.Message}");
            }
        }, ct);
    }

    private void SendBlocking(byte[] payload, CancellationToken ct)
    {
        try
        {
            using var port = CreatePort();
            port.Open();

            // Write in blocks: some USB-serial bridges drop bytes on a single huge transfer.
            const int chunkSize = 512;
            for (var offset = 0; offset < payload.Length; offset += chunkSize)
            {
                ct.ThrowIfCancellationRequested();

                var count = Math.Min(chunkSize, payload.Length - offset);
                port.Write(payload, offset, count);
            }

            // The printer needs the last byte clocked out before the port is torn down.
            port.BaseStream.Flush();
            Thread.Sleep(120);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new TransportException($"Serial write to {_portName} failed: {ex.Message}", ex);
        }

        _log.Debug($"Serial job of {payload.Length} bytes sent to {_portName}.");
    }

    private SerialPort CreatePort() => new(_portName, _baudRate, Parity.None, 8, StopBits.One)
    {
        Handshake = Handshake.None,
        DtrEnable = true,
        RtsEnable = true,
        WriteTimeout = 10_000,
        ReadTimeout = 2_000
    };
}
