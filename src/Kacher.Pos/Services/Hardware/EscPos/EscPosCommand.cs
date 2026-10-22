namespace Kacher.Pos.Services.Hardware.EscPos;

/// <summary>
/// Raw ESC/POS command vocabulary (Epson TM series reference).
///
/// ESC/POS is a byte protocol, not a driver API. Every command starts with one of four prefixes:
///   ESC = 0x1B, GS = 0x1D, FS = 0x1C, DLE = 0x10
/// followed by a mnemonic byte and 0..n parameter bytes. Because it is stateless at the transport
/// level, sending the bytes straight to the device avoids the Windows spooler entirely — that is
/// the whole point of this service: a receipt prints in ~180 ms instead of ~1.2 s through GDI.
///
/// Parameter ranges below are validated in <see cref="EscPosBuilder"/> so a bad setting can never
/// brick a printer mid-service.
/// </summary>
public static class EscPosCommand
{
    // ── Initialisation / control ──────────────────────────────────────────────────────────────
    public const byte Esc = 0x1B;
    public const byte Gs = 0x1D;
    public const byte Fs = 0x1C;
    public const byte Lf = 0x0A;
    public const byte Cr = 0x0D;
    public const byte Ht = 0x09;

    /// <summary>ESC @ — reset to power-on state. Always send once per job.</summary>
    public static readonly byte[] Initialize = { Esc, 0x40 };

    /// <summary>DLE EOT n — real-time status request (1 = printer, 2 = offline cause, 3 = error, 4 = paper).</summary>
    public static byte[] RealTimeStatus(byte n) => new byte[] { 0x10, 0x04, n };

    // ── Text formatting ───────────────────────────────────────────────────────────────────────
    /// <summary>ESC a n — 0 left, 1 centre, 2 right.</summary>
    public static byte[] Align(byte n) => new byte[] { Esc, 0x61, n };

    /// <summary>ESC E n — emphasised (bold) on/off.</summary>
    public static byte[] Bold(bool on) => new byte[] { Esc, 0x45, (byte)(on ? 1 : 0) };

    /// <summary>ESC - n — underline 0/1/2.</summary>
    public static byte[] Underline(byte n) => new byte[] { Esc, 0x2D, n };

    /// <summary>GS B n — white/black reverse printing, the classic "total" highlight bar.</summary>
    public static byte[] Reverse(bool on) => new byte[] { Gs, 0x42, (byte)(on ? 1 : 0) };

    /// <summary>GS ! n — character size. High nibble = width-1, low nibble = height-1 (0x11 = double both).</summary>
    public static byte[] Size(byte n) => new byte[] { Gs, 0x21, n };

    /// <summary>ESC t n — select code page (16 = WPC1252, 0 = CP437, 22 = CP864, 25 = WPC1256).</summary>
    public static byte[] CodePage(byte n) => new byte[] { Esc, 0x74, n };

    /// <summary>ESC 3 n — line spacing in dots (default 30 for 80 mm).</summary>
    public static byte[] LineSpacing(byte n) => new byte[] { Esc, 0x33, n };

    // ── Paper movement ───────────────────────────────────────────────────────────────────────
    /// <summary>ESC d n — print and feed n lines.</summary>
    public static byte[] FeedLines(byte n) => new byte[] { Esc, 0x64, n };

    /// <summary>ESC J n — print and feed n dots (fine-grained spacing used before the cutter).</summary>
    public static byte[] FeedDots(byte n) => new byte[] { Esc, 0x4A, n };

    /// <summary>GS V m n — cut. 66 = partial cut with feed, 65 = partial, 0 = full.</summary>
    public static byte[] Cut(bool partialWithFeed = true) =>
        new byte[] { Gs, 0x56, (byte)(partialWithFeed ? 66 : 65), 0x00 };

    /// <summary>ESC i — legacy full cut supported by older TM printers.</summary>
    public static readonly byte[] LegacyCut = { Esc, 0x69 };

    // ── Cash drawer ──────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// ESC p m t1 t2 — drawer kick.
    ///   m  = connector pin: 0 → pin 2, 1 → pin 5
    ///   t1 = ON time  × 2 ms   (25 → 50 ms)
    ///   t2 = OFF time × 2 ms   (250 → 500 ms)
    /// Epson recommends 50 ms on / 500 ms off; shorter pulses leave the solenoid latched and burn
    /// the coil, longer pulses can bounce the drawer open twice.
    /// </summary>
    public static byte[] DrawerKick(byte pin = 0, byte onTime = 25, byte offTime = 250) =>
        new byte[] { Esc, 0x70, pin, onTime, offTime };

    // ── Barcode ──────────────────────────────────────────────────────────────────────────────
    /// <summary>GS h n — barcode height in dots (1..255).</summary>
    public static byte[] BarcodeHeight(byte n) => new byte[] { Gs, 0x68, n };

    /// <summary>GS w n — module width (2..6).</summary>
    public static byte[] BarcodeWidth(byte n) => new byte[] { Gs, 0x77, n };

    /// <summary>GS H n — HRI (human readable) position: 0 none, 1 above, 2 below, 3 both.</summary>
    public static byte[] BarcodeHri(byte n) => new byte[] { Gs, 0x48, n };

    /// <summary>GS f n — HRI font: 0 = Font A, 1 = Font B.</summary>
    public static byte[] BarcodeFont(byte n) => new byte[] { Gs, 0x66, n };

    /// <summary>Barcode symbology identifiers used by <c>GS k</c>.</summary>
    public const byte SymbologyUpcA = 65;
    public const byte SymbologyUpcE = 66;
    public const byte SymbologyEan13 = 67;
    public const byte SymbologyEan8 = 68;
    public const byte SymbologyCode39 = 69;
    public const byte SymbologyItf = 70;
    public const byte SymbologyCode128 = 73;

    // ── QR code (2D symbol store) ────────────────────────────────────────────────────────────
    /// <summary>GS ( k — 2D symbol command family. Parameters are wrapped as (pL, pH) length prefix.</summary>
    public static readonly byte[] QrFunction = { Gs, 0x28, 0x6B };

    /// <summary>Select model 2 (the modern, widely supported QR model).</summary>
    public static readonly byte[] QrSelectModel2 = { Gs, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00 };

    /// <summary>Module size in dots; 1..16. 6 is the sweet spot on 80 mm receipts.</summary>
    public static byte[] QrModuleSize(byte n) => new byte[] { Gs, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, n };

    /// <summary>Error correction level: 48=L(~7%), 49=M(~15%), 50=Q(~25%), 51=H(~30%).</summary>
    public static byte[] QrErrorCorrection(byte level) => new byte[] { Gs, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, level };

    // ── Raster graphics ──────────────────────────────────────────────────────────────────────
    /// <summary>GS v 0 m xL xH yL yH — raster bit image. m=0 normal, 48/49 double width/height.</summary>
    public static byte[] RasterHeader(int widthBytes, int heightDots, byte mode = 0) =>
        new byte[]
        {
            Gs, 0x76, 0x30, mode,
            (byte)(widthBytes & 0xFF), (byte)((widthBytes >> 8) & 0xFF),
            (byte)(heightDots & 0xFF), (byte)((heightDots >> 8) & 0xFF)
        };
}
