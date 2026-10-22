using Kacher.Pos.Infrastructure;

namespace Kacher.Pos.Services.Hardware.Transports;

/// <summary>
/// Writes each job to a timestamped <c>.bin</c> file plus a readable <c>.txt</c> rendering.
/// </summary>
/// <remarks>
/// This is the safety net of the whole pipeline: when no printer is configured (or the hardware is
/// unplugged mid-service) the receipt is still captured, so a sale is never blocked and the ticket
/// can be reprinted later with <c>copy /b job.bin \\share\printer</c> or from the shift screen.
/// </remarks>
public sealed class FileSpoolTransport : IReceiptTransport
{
    private readonly string _directory;
    private readonly IAppLog _log;

    public FileSpoolTransport(IAppLog log, string? directory = null)
    {
        _log = log;
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kacher",
            "spool");
    }

    public string Description => $"File spool → {_directory}";

    public async Task SendAsync(byte[] payload, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(_directory);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var binaryPath = Path.Combine(_directory, $"job-{stamp}.bin");

            await File.WriteAllBytesAsync(binaryPath, payload, ct).ConfigureAwait(false);

            // A human-readable twin makes support calls trivial: "open the .txt we just wrote".
            var textPath = Path.Combine(_directory, $"job-{stamp}.txt");
            var preview = System.Text.Encoding.ASCII.GetString(payload)
                .Replace("\u001b", "<ESC>", StringComparison.Ordinal)
                .Replace("\u001d", "<GS>", StringComparison.Ordinal);

            await File.WriteAllTextAsync(textPath, preview, ct).ConfigureAwait(false);

            _log.Info($"Printer unavailable — job captured at {binaryPath}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TransportException($"Could not write the spool file: {ex.Message}", ex);
        }
    }

    public Task<TransportProbe> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            return Task.FromResult(TransportProbe.Ok($"Spool directory ready: {_directory}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(TransportProbe.Fail($"Spool directory not writable: {ex.Message}"));
        }
    }
}
