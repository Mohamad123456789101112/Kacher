using System.ComponentModel;
using System.Runtime.InteropServices;
using Kacher.Pos.Infrastructure;

namespace Kacher.Pos.Services.Hardware.Transports;

/// <summary>
/// Sends raw ESC/POS bytes through the Windows print spooler using the <c>RAW</c> data type.
/// </summary>
/// <remarks>
/// <para>
/// This is the transport used for both <c>WindowsDriver</c> and <c>Usb</c> connection types: a
/// USB ESC/POS printer always has a spooler queue on Windows, and writing to that queue in RAW mode
/// forwards our bytes untouched instead of letting a rendering driver rasterise a GDI page.
/// </para>
/// <para>
/// All Win32 calls here are synchronous, so the work is pushed onto the thread pool. That keeps the
/// UI thread free (the 60 fps requirement) without pretending the spooler API has an async form.
/// </para>
/// </remarks>
public sealed class WindowsRawPrinterTransport : IReceiptTransport
{
    private const int MaxChunk = 32 * 1024;

    private readonly string _printerName;
    private readonly IAppLog _log;

    public WindowsRawPrinterTransport(string printerName, IAppLog log)
    {
        _printerName = printerName;
        _log = log;
    }

    public string Description => $"Windows spooler (RAW) → \"{_printerName}\"";

    public Task SendAsync(byte[] payload, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromException(new TransportException("The Windows spooler transport requires Windows."));
        }

        if (string.IsNullOrWhiteSpace(_printerName))
        {
            return Task.FromException(new TransportException("No printer queue name is configured."));
        }

        // Task.Run keeps the blocking spooler calls off the dispatcher; the token is checked
        // between chunks so a cancelled job stops early instead of holding the queue.
        return Task.Run(() => SendBlocking(payload, ct), CancellationToken.None);
    }

    public Task<TransportProbe> ProbeAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(TransportProbe.Fail("Windows spooler unavailable on this OS."));
        }

        return Task.Run(() =>
        {
            if (!OpenPrinter(_printerName, out var handle, nint.Zero))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                return TransportProbe.Fail($"Queue \"{_printerName}\" not found: {error.Message}");
            }

            try
            {
                return TransportProbe.Ok($"Queue \"{_printerName}\" is available.");
            }
            finally
            {
                ClosePrinter(handle);
            }
        }, ct);
    }

    private void SendBlocking(byte[] payload, CancellationToken ct)
    {
        if (!OpenPrinter(_printerName, out var printer, nint.Zero))
        {
            throw new TransportException(
                $"Cannot open printer queue \"{_printerName}\": {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        try
        {
            var docInfo = new DocInfo1
            {
                DocumentName = "Kacher POS receipt",
                OutputFile = null,
                DataType = "RAW"
            };

            if (StartDocPrinter(printer, 1, ref docInfo) == 0)
            {
                throw new TransportException(
                    $"StartDocPrinter failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }

            try
            {
                StartPagePrinter(printer);

                try
                {
                    var offset = 0;
                    while (offset < payload.Length)
                    {
                        ct.ThrowIfCancellationRequested();

                        var chunk = Math.Min(MaxChunk, payload.Length - offset);
                        var buffer = new byte[chunk];
                        Buffer.BlockCopy(payload, offset, buffer, 0, chunk);

                        if (!WritePrinter(printer, buffer, chunk, out var written) || written != chunk)
                        {
                            throw new TransportException(
                                $"WritePrinter wrote {written}/{chunk} bytes: " +
                                $"{new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                        }

                        offset += chunk;
                    }
                }
                finally
                {
                    EndPagePrinter(printer);
                }
            }
            finally
            {
                EndDocPrinter(printer);
            }
        }
        finally
        {
            ClosePrinter(printer);
        }

        _log.Debug($"Spooled {payload.Length} bytes to \"{_printerName}\".");
    }

    // ── Win32 ───────────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string DocumentName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? OutputFile;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string printerName, out nint handle, nint defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(nint handle);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(nint handle, int level, ref DocInfo1 docInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(nint handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(nint handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(nint handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(nint handle, byte[] buffer, int count, out int written);
}
