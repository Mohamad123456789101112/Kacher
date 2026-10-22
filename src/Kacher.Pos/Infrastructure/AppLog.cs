using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Kacher.Pos.Data;

namespace Kacher.Pos.Infrastructure;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

public readonly record struct LogEntry(DateTime Timestamp, LogLevel Level, string Message, string? Exception);

public interface IAppLog
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);

    /// <summary>Recent entries, newest last — bound to the diagnostics flyout.</summary>
    IReadOnlyCollection<LogEntry> Recent { get; }

    event EventHandler<LogEntry>? EntryWritten;
}

/// <summary>
/// Thread-safe, fire-and-forget logger. Writes are serialised through a background queue so a slow
/// disk can never block the UI thread, and the last N entries stay in memory for the diagnostics UI.
/// </summary>
public sealed class AppLog : IAppLog, IDisposable
{
    private const int RingSize = 500;

    private readonly ConcurrentQueue<LogEntry> _ring = new();
    private readonly BlockingCollection<string> _pending = new(new ConcurrentQueue<string>(), 4096);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writer;
    private readonly object _fileGate = new();
    private string _logFile = string.Empty;

    public event EventHandler<LogEntry>? EntryWritten;

    public AppLog()
    {
        try
        {
            DatabaseConnectionFactory.EnsureDirectories();
            _logFile = Path.Combine(DatabaseConnectionFactory.LogDirectory, $"pos-{DateTime.Now:yyyyMMdd}.log");
        }
        catch (IOException)
        {
            _logFile = Path.Combine(Path.GetTempPath(), $"kacher-pos-{DateTime.Now:yyyyMMdd}.log");
        }

        _writer = Task.Run(WriteLoopAsync);
    }

    public IReadOnlyCollection<LogEntry> Recent => _ring.ToArray();

    public void Debug(string message) => Write(LogLevel.Debug, message, null);

    public void Info(string message) => Write(LogLevel.Info, message, null);

    public void Warn(string message) => Write(LogLevel.Warn, message, null);

    public void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);

    private void Write(LogLevel level, string message, Exception? ex)
    {
        var entry = new LogEntry(DateTime.Now, level, message, ex?.ToString());
        _ring.Enqueue(entry);
        while (_ring.Count > RingSize && _ring.TryDequeue(out _))
        {
            // trim
        }

        EntryWritten?.Invoke(this, entry);

        var line = new StringBuilder()
            .Append(entry.Timestamp.ToString("HH:mm:ss.fff")).Append(" [")
            .Append(level.ToString().ToUpperInvariant()).Append("] ")
            .Append(message)
            .AppendLine(entry.Exception is null ? string.Empty : Environment.NewLine + entry.Exception)
            .ToString();

        // Non-blocking: if the queue is saturated we drop the line rather than stall the caller.
        _pending.TryAdd(line);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            foreach (var line in _pending.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    await File.AppendAllTextAsync(_logFile, line, Encoding.UTF8, _cts.Token).ConfigureAwait(false);
                }
                catch (Exception) when (!_cts.IsCancellationRequested)
                {
                    // Logging must never throw into application code.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pending.CompleteAdding();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // shutdown race — nothing to do
        }
        _cts.Dispose();
        _pending.Dispose();
    }
}
