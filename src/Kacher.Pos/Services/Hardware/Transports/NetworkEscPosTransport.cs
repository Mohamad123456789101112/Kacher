using System.Net.Sockets;
using Kacher.Pos.Infrastructure;

namespace Kacher.Pos.Services.Hardware.Transports;

/// <summary>
/// Sends jobs to a network-attached printer over a raw TCP socket (the "port 9100" / JetDirect
/// protocol that every Ethernet thermal printer speaks).
/// </summary>
/// <remarks>
/// <para>
/// No driver and no spooler sit in the path, so latency is one round trip on the LAN and the
/// printer starts printing before the last byte arrives. That is what makes a busy till feel
/// instant compared with a 300–800 ms GDI spool cycle.
/// </para>
/// <para>
/// The job is written, the socket is shut down for sending (FIN) and then closed. Printer firmware
/// treats the FIN as "end of job", which is why no explicit length prefix is needed.
/// </para>
/// </remarks>
public sealed class NetworkEscPosTransport : IReceiptTransport
{
    private readonly string _host;
    private readonly int _port;
    private readonly IAppLog _log;
    private readonly TimeSpan _connectTimeout;

    public NetworkEscPosTransport(string host, int port, IAppLog log, TimeSpan? connectTimeout = null)
    {
        _host = host;
        _port = port <= 0 ? 9100 : port;
        _log = log;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
    }

    public string Description => $"Network ESC/POS → {_host}:{_port}";

    public async Task SendAsync(byte[] payload, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_connectTimeout);

        try
        {
            using var client = new TcpClient { NoDelay = true };

            await client.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);

            // Once connected the printer may be busy rendering the previous job; give the write a
            // longer budget than the connect attempt itself.
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            await using var stream = client.GetStream();
            await stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);

            client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TransportException($"Printer at {_host}:{_port} did not respond in time.");
        }
        catch (SocketException ex)
        {
            throw new TransportException($"Network error talking to {_host}:{_port} — {ex.Message}", ex);
        }

        _log.Debug($"Sent {payload.Length} bytes to {_host}:{_port}.");
    }

    public async Task<TransportProbe> ProbeAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
            return TransportProbe.Ok($"Connected to {_host}:{_port}.");
        }
        catch (OperationCanceledException)
        {
            return TransportProbe.Fail($"No response from {_host}:{_port} within 2 s.");
        }
        catch (SocketException ex)
        {
            return TransportProbe.Fail($"{_host}:{_port} refused the connection — {ex.SocketErrorCode}.");
        }
    }
}
