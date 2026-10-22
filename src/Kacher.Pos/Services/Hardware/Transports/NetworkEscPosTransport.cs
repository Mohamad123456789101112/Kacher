using System.Diagnostics;
using System.Net.Sockets;

namespace Kacher.Pos.Services.Hardware.Transports;

/// <summary>
/// Raw TCP transport for Ethernet/Wi-Fi thermal printers. ESC/POS over TCP uses port 9100
/// ("JetDirect"/"raw printing") — no handshake, no framing, the printer consumes the socket bytes
/// as a command stream and closes the connection when the job is done.
///
/// Implementation notes:
/// * <c>NoDelay = true</c> disables Nagle so a 2 KB receipt leaves in one segment.
/// * A connect timeout is enforced with a linked CTS — <c>TcpClient.ConnectAsync</c> alone can hang
///   for the Windows default SYN timeout (~21 s), which would freeze the "Process &amp; Print" button.
/// * The transport is created per print job and disposed immediately: printers accept one raw
///   connection at a time, so holding a socket open blocks the second till.
/// </summary>
public sealed class NetworkEscPosTransport : IReceiptTransport
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;

    public NetworkEscPosTransport(string host, int port = 9100)
    {
        _host = host;
        _port = port <= 0 ? 9100 : port;
    }

    public string Description => $"Network {_host}:{_port}";

    public bool IsConnected => _client?.Connected == true;

    private async Task EnsureConnectedAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_client?.Connected == true && _stream is not null)
        {
            return;
        }

        DisposeClient();

        var client = new TcpClient
        {
            NoDelay = true,
            SendTimeout = (int)timeout.TotalMilliseconds
        };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        try
        {
            await client.ConnectAsync(_host, _port, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Printer at {_host}:{_port} did not accept a connection within {timeout.TotalSeconds:0.#} s.");
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new IOException($"Cannot reach printer at {_host}:{_port} ({ex.SocketErrorCode}).", ex);
        }

        _client = client;
        _stream = client.GetStream();
    }

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

        // A single WriteAsync keeps the payload in one TCP segment; the printer buffers it all
        // before executing, which avoids the half-printed-receipt failure mode on flaky Wi-Fi.
        await _stream!.WriteAsync(payload, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task<TransportProbe> ProbeAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await EnsureConnectedAsync(TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
            sw.Stop();
            return TransportProbe.Online($"Reachable ({sw.ElapsedMilliseconds} ms)", sw.Elapsed);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or SocketException)
        {
            sw.Stop();
            return TransportProbe.Offline(ex.Message);
        }
    }

    private void DisposeClient()
    {
        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;
    }

    public ValueTask DisposeAsync()
    {
        DisposeClient();
        return ValueTask.CompletedTask;
    }
}
