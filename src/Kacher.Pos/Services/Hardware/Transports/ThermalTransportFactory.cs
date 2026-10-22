using System.Printing;
using Kacher.Pos.Models;

namespace Kacher.Pos.Services.Hardware.Transports;

public interface IReceiptTransportFactory
{
    /// <summary>Builds the transport that matches the active hardware profile.</summary>
    IReceiptTransport Create(HardwareConfig config);

    /// <summary>True when the configured Windows queue actually exists on this machine.</summary>
    bool PrinterQueueExists(string printerName);
}

public sealed class ThermalTransportFactory : IReceiptTransportFactory
{
    public IReceiptTransport Create(HardwareConfig config) => config.ConnectionType switch
    {
        PrinterConnectionType.Network => new NetworkEscPosTransport(
            string.IsNullOrWhiteSpace(config.NetworkHost) ? "127.0.0.1" : config.NetworkHost,
            config.NetworkPort),

        PrinterConnectionType.Serial => new SerialEscPosTransport(
            string.IsNullOrWhiteSpace(config.SerialPortName) ? "COM1" : config.SerialPortName,
            config.BaudRate),

        PrinterConnectionType.Usb => new UsbEscPosTransport(config.UsbVendorProductId),

        _ => new WindowsRawPrinterTransport(
            string.IsNullOrWhiteSpace(config.ThermalPrinterName) ? "Microsoft Print to PDF" : config.ThermalPrinterName)
    };

    /// <summary>
    /// Enumerates installed spooler queues. Cheap enough to call when the hardware settings screen
    /// opens, but never on the sale hot-path — hence it is not used inside <see cref="Create"/>.
    /// </summary>
    public bool PrinterQueueExists(string printerName)
    {
        if (string.IsNullOrWhiteSpace(printerName))
        {
            return false;
        }

        try
        {
            using var server = new LocalPrintServer();
            using var queues = server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections });
            return queues.Any(q => string.Equals(q.FullName, printerName, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(q.Name, printerName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // Print Spooler service stopped — treat as "unknown", the print attempt will surface it.
            return false;
        }
    }
}
