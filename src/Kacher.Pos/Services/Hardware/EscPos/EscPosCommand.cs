namespace Kacher.Pos.Services.Hardware.EscPos;

/// <summary>
/// Raw ESC/POS command bytes. Every receipt the terminal prints is just a byte stream made of
/// these sequences followed by text encoded in the printer's active code page.
/// </summary>
/// <remarks>
/// ESC/POS is the de-facto standard command set for Epson and Epson-compatible thermal printers
/// (TM-T20/T82, Xprinter XP-80C, Rongta RP80, Bixolon SRP-350 …). A `0x1B` byte introduces most
/// commands, `0x1D` introduces "group 2" commands (barcodes, QR, raster images).
/// </remarks>
public static class EscPosCommand
{
    public const byte Esc = 0x1B;
    public const byte Gs = 0x1D;
    public const byte Lf = 0x0A;
    public const byte Cr = 0x0D;
    public const byte Ht = 0x09;
    public const byte Ff = 0x0C;

    // ── Initialise / reset ─────────────────────────────────────────────────────────────────────
    /// <summary>ESC @ — reset printer to power-on defaults (font, alignment, line spacing).</summary>
    public static ReadOnlySpan<byte> Initialise => [Esc, 0x40];

    /// <summary>DLE EOT n — real-time status request (n=1 printer status, n=2 offline, n=4 paper).</summary>
    public static byte[] StatusRequest(byte type = 1) => [0x10, 0x04, type];

    // ── Text formatting ───────────────────────────────────────────────────────────────────────
    /// <summary>ESC a n — align: 0 left, 1 centre, 2 right.</summary>
    public static byte[] Align(byte mode) => [Esc, 0x61, mode];

    /// <summary>ESC E n — emphasised (bold) on/off.</summary>
    public static byte[] Bold(bool on) => [Esc, 0x45, (byte)(on ? 1 : 0)];

    /// <summary>ESC - n — underline: 0 off, 1 thin, 2 thick.</summary>
    public static byte[] Underline(byte mode) => [Esc, 0x2D, mode];

    /// <summary>
    /// GS ! n — character size. The high nibble is the width multiplier and the low nibble the
    /// height multiplier, each 1..8 (encoded as 0..7).
    /// </summary>
    public static byte[] TextSize(int width, int height)
    {
        var w = (byte)(Math.Clamp(width, 1, 8) - 1);
        var h = (byte)(Math.Clamp(height, 1, 8) - 1);
        return [Gs, 0x21, (byte)((w << 4) | h)];
    }

    /// <summary>ESC M n — Font A (0, 12×24) or Font B (1, 9×17, narrower → more columns).</summary>
    public static byte[] Font(byte font) => [Esc, 0x4D, font];

    /// <summary>ESC t n — select the character code table (code page).</summary>
    public static byte[] CodePage(byte codePage) => [Esc, 0x74, codePage];

    /// <summary>ESC 3 n — line spacing in dots (24 is the default for Font A).</summary>
    public static byte[] LineSpacing(byte dots) => [Esc, 0x33, dots];

    // ── Paper ─────────────────────────────────────────────────────────────────────────────────
    /// <summary>ESC d n — feed n lines.</summary>
    public static byte[] Feed(byte lines) => [Esc, 0x64, lines];

    /// <summary>GS V m n — cut. m=0 full cut, m=1 partial cut; n is the feed-before-cut distance.</summary>
    public static byte[] Cut(bool partial = false, byte feed = 3) => [Gs, 0x56, (byte)(partial ? 1 : 0), feed];

    // ── Cash drawer ───────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// ESC p m t1 t2 — pulse the drawer kick connector.
    /// <para>m=0 → pin 2 (the standard drawer), m=1 → pin 5.</para>
    /// <para>t1/t2 are the on/off pulse widths in 2 ms units; 25 and 250 → 50 ms on / 500 ms off,
    /// which is the Epson recommended drive for a 24 V solenoid.</para>
    /// </summary>
    public static byte[] OpenDrawer(byte pin = 0, byte onTime = 25, byte offTime = 250)
        => [Esc, 0x70, pin, onTime, offTime];

    // ── Barcodes ──────────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// GS H n / GS f n / GS h n / GS w n / GS k — CODE 128 (m=73) with HRI text below the bars.
    /// </summary>
    public static byte[] Code128(string data, byte moduleWidth = 2, byte height = 80)
    {
        // GS k takes a single length byte, so cap the payload at 255 characters.
        var ascii = System.Text.Encoding.ASCII.GetBytes(data);
        var payload = ascii.Length > 255 ? ascii[..255] : ascii;

        return
        [
            Gs, 0x48, 0x02,      // HRI below the barcode
            Gs, 0x66, 0x00,      // HRI in Font A
            Gs, 0x68, height,    // bar height in dots
            Gs, 0x77, moduleWidth,
            Gs, 0x6B, 73, (byte)payload.Length, .. payload
        ];
    }

    // ── QR code ──────────────────────────────────────────────────────────────────────────────
    // GS ( k has a 4-byte parameter block: pL pH cn fn [params]; pL/pH are the little-endian
    // length of "cn fn params" only, so every helper below recomputes them.

    /// <summary>GS ( k — select QR model 2, set module size and error correction.</summary>
    public static byte[] QrConfigure(byte moduleSize = 6, byte errorCorrection = 49)
    {
        // Error correction: 48=L 7%, 49=M 15%, 50=Q 25%, 51=H 30%.
        var m = Math.Clamp(moduleSize, 1, 16);
        return
        [
            Gs, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00, // model 2
            Gs, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, m,          // module size
            Gs, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, errorCorrection
        ];
    }

    /// <summary>GS ( k — store <paramref name="data"/> in the symbol buffer (fn 080).</summary>
    public static byte[] QrStore(string data)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(data);
        var length = payload.Length + 3;

        return
        [
            Gs, 0x28, 0x6B, (byte)(length & 0xFF), (byte)((length >> 8) & 0xFF),
            0x31, 0x50, 0x30, .. payload
        ];
    }

    /// <summary>GS ( k — print the stored symbol (fn 081).</summary>
    public static ReadOnlySpan<byte> QrPrint => [Gs, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30];

    // ── Raster graphics ──────────────────────────────────────────────────────────────────────
    /// <summary>
    /// GS v 0 m xL xH yL yH — print a raster bitmap supplied as 1-bit, row-major data.
    /// </summary>
    public static byte[] RasterImage(int widthDots, int heightDots, byte[] bitmap)
    {
        var bytesPerRow = (widthDots + 7) / 8;

        return
        [
            Gs, 0x76, 0x30, 0x00,
            (byte)(bytesPerRow & 0xFF), (byte)((bytesPerRow >> 8) & 0xFF),
            (byte)(heightDots & 0xFF), (byte)((heightDots >> 8) & 0xFF),
            .. bitmap
        ];
    }
}
