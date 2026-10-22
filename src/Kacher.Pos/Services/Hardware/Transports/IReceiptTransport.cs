using System.IO;

namespace Kacher.Pos.Services.Hardware.Transports;

/// <summary>Result of a non-destructive connectivity probe (used by the status bar pills).</summary>
public sealed record TransportProbe(bool IsAvailable, string Message, TimeSpan Latency)
{
    public static TransportProbe Offline(string message) => new(false, message, TimeSpan.Zero);
    public static TransportProbe Online(string message, TimeSpan latency) => new(true, message, latency);
}

/// <summary>
/// A byte pipe to a receipt printer. Implementations are intentionally dumb: they accept a fully
/// built ESC/POS payload and return once the device acknowledged the write.
/// </summary>
public interface IReceiptTransport : IAsyncDisposable
{
    /// <summary>Short label for the UI, e.g. "Network 192.168.1.50:9100".</summary>
    string Description { get; }

    bool IsConnected { get; }

    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default);

    Task<TransportProbe> ProbeAsync(CancellationToken ct = default);
}

/// <summary>
/// Development / commissioning transport: writes the exact byte stream to a .bin file under
/// <c>%LOCALAPPDATA%\Kacher\spool</c>. Lets a technician diff what the app *would* have sent
/// without wasting paper, and lets CI verify receipt layout without hardware.
/// </summary>
public sealed class FileSpoolTransport : IReceiptTransport
{
    private readonly string _directory;

    public FileSpoolTransport(string? directory = null)
    {
        _directory = directory ?? Data.DatabaseConnectionFactory.SpoolDirectory;
        Directory.CreateDirectory(_directory);
    }

    public string Description => $"File spool ({_directory})";

    public bool IsConnected => true;

    public async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var path = Path.Combine(_directory, $"receipt-{DateTime.Now:yyyyMMdd-HHmmss-fff}.bin");
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public Task<TransportProbe> ProbeAsync(CancellationToken ct = default) =>
        Task.FromResult(TransportProbe.Online($"Spooling to {_directory}", TimeSpan.Zero));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
