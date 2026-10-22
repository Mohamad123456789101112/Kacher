using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kacher.Pos.Services.Hardware.Transports;

/// <summary>
/// Direct USB transport — the fastest possible path, with no spooler and no vendor driver.
///
/// HOW IT WORKS
/// A USB receipt printer exposes the USB Printer Class interface
/// <c>{28D78FAD-5A12-11D1-AE5B-0000F803A8C2}</c>. Windows binds <c>usbprint.sys</c> to it, which
/// creates a device interface path of the form
/// <c>\\?\usb#vid_04b8&amp;pid_0e15#...</c>. <c>CreateFile</c> on that path, followed by
/// <c>WriteFile</c>, hands bytes to the printer's bulk-OUT pipe without any intermediate rendering.
///
/// CAVEATS (documented deliberately):
/// * This requires the device to be bound to <c>usbprint.sys</c>. If a vendor driver has claimed the
///   device (common after installing Epson's APD), use <see cref="WindowsRawPrinterTransport"/>
///   instead — that path goes through the vendor queue but still sends RAW bytes.
/// * Only one process may hold the handle; the transport therefore opens and closes per job.
/// * Windows enforces a 4 KB-ish write chunk on some host controllers, so payloads are chunked.
///
/// If enumeration finds no device, the transport fails with an actionable message instead of
/// silently discarding a receipt.
/// </summary>
public sealed class UsbEscPosTransport : IReceiptTransport
{
    private static readonly Guid UsbPrintInterface = new("28D78FAD-5A12-11D1-AE5B-0000F803A8C2");

    private readonly string? _vendorProductFilter;

    /// <param name="vendorProductId">Optional "VID:PID" filter, e.g. "04B8:0E15".</param>
    public UsbEscPosTransport(string? vendorProductId = null) => _vendorProductFilter = vendorProductId;

    public string Description => $"USB direct ({_vendorProductFilter ?? "first matching printer"})";

    public bool IsConnected => FindDevicePaths().Count > 0;

    public Task SendAsync(byte[] payload, CancellationToken ct = default)
    {
        // CreateFile/WriteFile are blocking Win32 calls, so the whole job runs on the thread pool.
        return Task.Run(() =>
        {
            var path = FindDevicePaths().FirstOrDefault()
                       ?? throw new TransportException(
                           "No USB printer interface found. Bind the printer to usbprint.sys, or switch " +
                           "the connection type to Windows driver / Network.");

            WriteToDevice(path, payload);
        }, CancellationToken.None);
    }

    public Task<TransportProbe> ProbeAsync(CancellationToken ct = default)
    {
        var devices = FindDevicePaths();

        return Task.FromResult(devices.Count == 0
            ? TransportProbe.Fail("No USB printer interface detected.")
            : TransportProbe.Ok($"{devices.Count} USB printer interface(s) found."));
    }

    // ── Device I/O ───────────────────────────────────────────────────────────────────────────

    private static void WriteToDevice(string devicePath, byte[] payload)
    {
        using var handle = CreateFile(
            devicePath,
            GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"Cannot open USB printer '{devicePath}'.");
        }

        const int chunkSize = 4096;
        for (var offset = 0; offset < payload.Length; offset += chunkSize)
        {
            var size = Math.Min(chunkSize, payload.Length - offset);
            var chunk = new byte[size];
            Array.Copy(payload, offset, chunk, 0, size);

            if (!WriteFile(handle, chunk, (uint)size, out var written, IntPtr.Zero) || written != size)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "USB write failed — the printer may be busy or claimed by another application.");
            }
        }
    }

    /// <summary>Enumerates USB printer interfaces, optionally filtered by VID/PID.</summary>
    private List<string> FindDevicePaths()
    {
        var results = new List<string>();
        var guid = UsbPrintInterface;

        var deviceInfoSet = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
        {
            return results;
        }

        try
        {
            var interfaceData = new SpDeviceInterfaceData
            {
                CbSize = Marshal.SizeOf<SpDeviceInterfaceData>()
            };

            for (var index = 0; SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref guid, index, ref interfaceData); index++)
            {
                var path = GetDevicePath(deviceInfoSet, ref interfaceData);
                if (path is null)
                {
                    continue;
                }

                if (_vendorProductFilter is null ||
                    path.Contains(NormaliseFilter(_vendorProductFilter), StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(path);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return results;
    }

    /// <summary>Converts "04B8:0E15" / "04B8-0E15" into the device-path fragment "vid_04b8&amp;pid_0e15".</summary>
    private static string NormaliseFilter(string value)
    {
        var cleaned = value.Trim().Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);

        if (cleaned.Contains("vid_", StringComparison.OrdinalIgnoreCase))
        {
            return cleaned.ToLowerInvariant();
        }

        var parts = cleaned.Split(new[] { ':', '-', '&' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return cleaned.ToLowerInvariant();
        }

        return $"vid_{parts[0].ToLowerInvariant()}&pid_{parts[1].ToLowerInvariant()}";
    }

    private static string? GetDevicePath(IntPtr deviceInfoSet, ref SpDeviceInterfaceData interfaceData)
    {
        // Two-call pattern: first query the required buffer size, then read the detail structure.
        SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W is 4 + sizeof(WCHAR) (6 on x64 with padding).
            Marshal.WriteInt32(buffer, 0, IntPtr.Size == 8 ? 8 : 6);

            if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, buffer, required, out _, IntPtr.Zero))
            {
                return null;
            }

            // DevicePath starts at offset 4; read as a Unicode string until NUL.
            return Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ── Interop ──────────────────────────────────────────────────────────────────────────────

    private const int DigcfPresent = 0x02;
    private const int DigcfDeviceInterface = 0x10;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        int memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    private static SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile) =>
        CreateFileW(fileName, desiredAccess, shareMode, securityAttributes, creationDisposition,
            flagsAndAttributes, templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle file, byte[] buffer, uint numberOfBytesToWrite,
        out uint numberOfBytesWritten, IntPtr overlapped);
}
