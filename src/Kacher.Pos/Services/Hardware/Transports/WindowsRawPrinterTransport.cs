using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Kacher.Pos.Services.Hardware.Transports;

/// <summary>
/// Sends the ESC/POS payload through the Windows print spooler as a <b>RAW</b> job.
///
/// Why bother, when a "driver" is supposed to render? Because the vendor driver still needs to be
/// installed for the queue to exist, but we bypass its rendering: <c>pDatatype = "RAW"</c> tells the
/// spooler to pass our bytes to the device untouched. This is the most compatible path — it works
/// with USB, LPT, and any vendor port monitor — while still skipping GDI text rendering.
///
/// The winspool calls are synchronous by nature, so the whole job runs on a pool thread.
/// </summary>
public sealed class WindowsRawPrinterTransport : IReceiptTransport
{
    private readonly string _printerName;

    public WindowsRawPrinterTransport(string printerName) => _printerName = printerName;

    public string Description => $"Windows spooler (RAW) → {_printerName}";

    public bool IsConnected => !string.IsNullOrWhiteSpace(_printerName);

    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var bytes = payload.ToArray();
        var documentName = $"Kacher POS {DateTime.Now:HH:mm:ss}";

        return Task.Run(() => SendRaw(_printerName, documentName, bytes), ct);
    }

    public Task<TransportProbe> ProbeAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_printerName))
        {
            return Task.FromResult(TransportProbe.Offline("No printer queue configured."));
        }

        return Task.Run(() =>
        {
            if (!OpenPrinter(_printerName, out var handle, IntPtr.Zero))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                return TransportProbe.Offline($"Queue unreachable: {error.Message}");
            }

            ClosePrinter(handle);

            // A queue with jobs parked in "Error" state is still reachable, so surface job count.
            var pending = CountPendingJobs(_printerName);
            return TransportProbe.Online(
                pending > 0 ? $"Queue ready ({pending} pending)" : "Queue ready", TimeSpan.Zero);
        }, ct);
    }

    private static void SendRaw(string printerName, string documentName, byte[] bytes)
    {
        if (!OpenPrinter(printerName, out var printer, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open printer '{printerName}'.");
        }

        try
        {
            var docInfo = new DocInfo1
            {
                DocumentName = documentName,
                OutputFile = null,
                DataType = "RAW"
            };

            if (StartDocPrinter(printer, 1, ref docInfo) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "StartDocPrinter failed.");
            }

            try
            {
                if (!StartPagePrinter(printer))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "StartPagePrinter failed.");
                }

                try
                {
                    if (!WritePrinter(printer, bytes, bytes.Length, out var written) || written != bytes.Length)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(),
                            $"WritePrinter wrote {written}/{bytes.Length} bytes.");
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
    }

    private static int CountPendingJobs(string printerName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                $@"Software\Kacher\PrintQueues\{printerName}");
            return key?.GetValue("PendingJobs") is int count ? count : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // ── winspool.drv ─────────────────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocumentName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string printerName, out IntPtr printer, IntPtr defaults);

    [DllImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr printer);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int StartDocPrinter(IntPtr printer, int level, ref DocInfo1 docInfo);

    [DllImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr printer);

    [DllImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr printer);

    [DllImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr printer);

    [DllImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr printer, byte[] buffer, int count, out int written);
}
