using Kacher.Pos.Infrastructure;
using Kacher.Pos.Models;

namespace Kacher.Pos.Services.Hardware.Transports;

/// <summary>Creates the transport that matches the active <see cref="HardwareConfig"/>.</summary>
public interface IReceiptTransportFactory
{
    /// <summary>The primary path for the configured connection type.</summary>
    IReceiptTransport Create(HardwareConfig config);

    /// <summary>
    /// Ordered candidates: the configured transport first, then every other plausible path, ending
    /// with the file spool so a job can always be captured.
    /// </summary>
    IReadOnlyList<IReceiptTransport> CreateFallbacks(HardwareConfig config);
}

public sealed class ThermalTransportFactory : IReceiptTransportFactory
{
    private readonly IAppLog _log;

    public ThermalTransportFactory(IAppLog log) => _log = log;

    public IReceiptTransport Create(HardwareConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.ConnectionType switch
        {
            PrinterConnectionType.Network => CreateNetwork(config),
            PrinterConnectionType.Serial => CreateSerial(config),
            PrinterConnectionType.Usb => new UsbEscPosTransport(config.UsbVendorProductId),
            _ => CreateSpooler(config)
        };
    }

    public IReadOnlyList<IReceiptTransport> CreateFallbacks(HardwareConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var candidates = new List<IReceiptTransport> { Create(config) };

        // A USB printer is nearly always reachable through the spooler as well, and a network
        // printer sometimes answers on the queue name too — trying both costs milliseconds.
        // Direct USB needs usbprint.sys; a vendor driver (Epson APD) claims the device instead, so
        // the spooler queue is always the second attempt for a USB printer.
        if (config.ConnectionType != PrinterConnectionType.WindowsDriver && !string.IsNullOrWhiteSpace(config.ThermalPrinterName))
        {
            candidates.Add(CreateSpooler(config));
        }

        if (config.ConnectionType != PrinterConnectionType.Network && !string.IsNullOrWhiteSpace(config.NetworkHost))
        {
            candidates.Add(CreateNetwork(config));
        }

        if (config.ConnectionType != PrinterConnectionType.Serial && !string.IsNullOrWhiteSpace(config.SerialPortName))
        {
            candidates.Add(CreateSerial(config));
        }

        candidates.Add(new FileSpoolTransport(_log));
        return candidates;
    }

    /// <summary>
    /// True when a Windows spooler queue with this name exists. Used by the hardware screen to warn
    /// about a typo before a test page is spooled into the void.
    /// </summary>
    public bool PrinterQueueExists(string? printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var server = new System.Printing.LocalPrintServer();
            using var queues = server.GetPrintQueues(new[]
            {
                System.Printing.EnumeratedPrintQueueTypes.Local,
                System.Printing.EnumeratedPrintQueueTypes.Connections
            });

            return queues.Any(q => string.Equals(q.FullName, printerName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is System.Printing.PrintQueueException or InvalidOperationException)
        {
            _log.Warn($"Could not enumerate print queues: {ex.Message}");
            return false;
        }
    }

    private IReceiptTransport CreateNetwork(HardwareConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.NetworkHost))
        {
            throw new TransportException(
                "Network printer selected but no host is configured in Hardware settings.");
        }

        return new NetworkEscPosTransport(config.NetworkHost, config.NetworkPort, _log);
    }

    private IReceiptTransport CreateSerial(HardwareConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SerialPortName))
        {
            throw new TransportException(
                "Serial printer selected but no COM port is configured in Hardware settings.");
        }

        return new SerialEscPosTransport(config.SerialPortName, config.BaudRate, _log);
    }

    private IReceiptTransport CreateSpooler(HardwareConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ThermalPrinterName))
        {
            throw new TransportException(
                "No printer queue is configured. Pick one in Hardware settings.");
        }

        return new WindowsRawPrinterTransport(config.ThermalPrinterName, _log);
    }
}
