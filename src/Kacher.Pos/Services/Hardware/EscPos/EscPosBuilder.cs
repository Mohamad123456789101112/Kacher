using System.Globalization;
using System.Text;

namespace Kacher.Pos.Services.Hardware.EscPos;

/// <summary>
/// Fluent, allocation-conscious builder for ESC/POS byte streams.
///
/// Every receipt is assembled in memory (a 48-column receipt is ~2-6 KB) and flushed to the
/// transport with a single <c>WriteAsync</c>. Sending one buffer instead of 200 small writes is what
/// keeps the print job under 200 ms — each USB/TCP write has kernel round-trip overhead.
///
/// The builder is NOT thread-safe by design; create one per print job.
/// </summary>
public sealed class EscPosBuilder
{
    private readonly MemoryStream _buffer = new(4096);
    private readonly Encoding _encoding;
    private bool _initialised;

    /// <param name="columns">Font A columns for the roll width (32 for 58 mm, 48 for 80 mm).</param>
    /// <param name="encoding">Code page encoding matching <see cref="EscPosCommand.CodePage"/>.</param>
    public EscPosBuilder(int columns, Encoding encoding)
    {
        Columns = Math.Clamp(columns, 20, 64);
        _encoding = encoding;
    }

    public int Columns { get; }

    /// <summary>Bytes written so far — surfaced in the hardware diagnostics panel.</summary>
    public int Length => (int)_buffer.Length;

    // ── Low level ────────────────────────────────────────────────────────────────────────────

    public EscPosBuilder Raw(ReadOnlySpan<byte> bytes)
    {
        _buffer.Write(bytes);
        return this;
    }

    public EscPosBuilder Raw(params byte[] bytes) => Raw(bytes.AsSpan());

    /// <summary>ESC @ must be the first thing the printer sees; enforced automatically.</summary>
    private EscPosBuilder EnsureInit()
    {
        if (!_initialised)
        {
            Raw(EscPosCommand.Initialize);
            _initialised = true;
        }
        return this;
    }

    public EscPosBuilder Init(byte codePage = 16)
    {
        _buffer.SetLength(0);
        _initialised = true;
        Raw(EscPosCommand.Initialize);
        Raw(EscPosCommand.CodePage(codePage));
        return this;
    }

    // ── Formatting ───────────────────────────────────────────────────────────────────────────

    public EscPosBuilder AlignLeft() => Raw(EscPosCommand.Align(0));

    public EscPosBuilder AlignCenter() => Raw(EscPosCommand.Align(1));

    public EscPosBuilder AlignRight() => Raw(EscPosCommand.Align(2));

    public EscPosBuilder Bold(bool on = true) => Raw(EscPosCommand.Bold(on));

    public EscPosBuilder Underline(bool on = true) => Raw(EscPosCommand.Underline((byte)(on ? 1 : 0)));

    public EscPosBuilder Reverse(bool on = true) => Raw(EscPosCommand.Reverse(on));

    /// <summary>Sets character width/height multipliers (1..8 each). Encoded as GS ! n.</summary>
    public EscPosBuilder Size(int widthMultiplier = 1, int heightMultiplier = 1)
    {
        var w = (byte)(Math.Clamp(widthMultiplier, 1, 8) - 1);
        var h = (byte)(Math.Clamp(heightMultiplier, 1, 8) - 1);
        return Raw(EscPosCommand.Size((byte)((w << 4) | h)));
    }

    public EscPosBuilder ResetStyle()
    {
        Raw(EscPosCommand.Bold(false));
        Raw(EscPosCommand.Underline(0));
        Raw(EscPosCommand.Reverse(false));
        Raw(EscPosCommand.Size(0));
        return AlignLeft();
    }

    /// <summary>ESC 3 n — tighter line spacing for multi-line addresses.</summary>
    public EscPosBuilder LineSpacing(byte dots) => Raw(EscPosCommand.LineSpacing(dots));

    // ── Text ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes text with the configured code page. Characters the printer's ROM cannot render are
    /// replaced with '?' rather than emitting a byte that would print as a random glyph.
    /// </summary>
    private byte[] Encode(string text)
    {
        var sanitized = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\r')
            {
                continue;
            }

            sanitized.Append(ch == '\n' ? '\n' : (ch == '\t' ? ' ' : ch));
        }

        var bytes = _encoding.GetBytes(sanitized.ToString());

        // Round-trip check: unmappable characters come back as '?' from most single-byte encodings.
        // Bounded by both lengths so a hypothetical multi-byte code page cannot index out of range.
        var checkLength = Math.Min(bytes.Length, sanitized.Length);
        for (var i = 0; i < checkLength; i++)
        {
            if (bytes[i] == 0x3F && sanitized[i] != '?')
            {
                bytes[i] = (byte)'?';
            }
        }

        return bytes;
    }

    /// <summary>Writes text plus a line feed. Never use <c>WriteLine</c> — the printer expects CR/LF bytes.</summary>
    public EscPosBuilder Line(string text = "")
    {
        EnsureInit();
        Raw(Encode(text));
        Raw(EscPosCommand.Lf);
        return this;
    }

    public EscPosBuilder Text(string text)
    {
        EnsureInit();
        Raw(Encode(text));
        return this;
    }

    public EscPosBuilder Centered(string text)
    {
        AlignCenter();
        Line(text);
        return AlignLeft();
    }

    /// <summary>Hard-wrapped paragraph; honours the roll width so nothing is clipped.</summary>
    public EscPosBuilder Wrap(string text, bool center = false)
    {
        if (center)
        {
            AlignCenter();
        }

        foreach (var line in WrapLines(text, Columns))
        {
            Line(line);
        }

        if (center)
        {
            AlignLeft();
        }

        return this;
    }

    /// <summary>Repeats a character across the full roll width.</summary>
    public EscPosBuilder Rule(char c = '-')
    {
        EnsureInit();
        return Line(new string(c, Columns));
    }

    /// <summary>
    /// Left/right justified pair — the standard way to print "ITEM ......... 12.00" on a receipt.
    /// If the left text would collide with the right column, it is truncated with an ellipsis.
    /// </summary>
    public EscPosBuilder TwoColumn(string left, string right, char pad = ' ')
    {
        var rightText = right ?? string.Empty;
        var available = Columns - rightText.Length - 1;

        if (available < 1)
        {
            return Line(rightText);
        }

        var leftText = left ?? string.Empty;
        if (leftText.Length > available)
        {
            leftText = leftText.Length <= available - 1
                ? leftText
                : leftText[..Math.Max(0, available - 1)] + "~";
        }

        var gap = Math.Max(1, Columns - leftText.Length - rightText.Length);
        return Line(leftText + new string(pad, gap) + rightText);
    }

    /// <summary>Three column layout used for invoice lines: name, qty, amount.</summary>
    public EscPosBuilder ThreeColumn(string left, string middle, string right)
    {
        var m = middle.Length;
        var r = right.Length;
        var available = Columns - m - r - 2;

        if (available < 4)
        {
            return Line($"{left} {middle} {right}");
        }

        var l = left.Length > available ? left[..Math.Max(0, available - 1)] + "~" : left;
        var gap1 = Math.Max(1, available - l.Length + 1);
        return Line(l + new string(' ', gap1) + m + " " + right);
    }

    /// <summary>Money format fixed to the receipt width (no currency locale surprises).</summary>
    public static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    // ── Symbols ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Prints a 1D barcode. CODE128 is used by default: it encodes digits at ~11 modules per
    /// character and is decodable by every modern scanner, unlike the older EAN families.
    /// </summary>
    public EscPosBuilder Barcode(string data, byte symbology = EscPosCommand.SymbologyCode128, byte height = 60)
    {
        EnsureInit();

        if (string.IsNullOrWhiteSpace(data))
        {
            return this;
        }

        // Length is a single byte for the GS k m n form.
        var payload = _encoding.GetBytes(data);
        if (payload.Length is 0 or > 255)
        {
            return this;
        }

        Raw(EscPosCommand.BarcodeHeight(height));
        Raw(EscPosCommand.BarcodeWidth(2));
        Raw(EscPosCommand.BarcodeHri(2)); // HRI below the bars
        Raw(EscPosCommand.BarcodeFont(0));
        Raw(EscPosCommand.Gs, 0x6B, symbology, (byte)payload.Length);
        Raw(payload);
        return this;
    }

    /// <summary>
    /// Prints a QR code using the GS ( k symbol store commands.
    /// Sequence: select model 2 → module size → error correction → store data → print.
    /// The length prefix for "store data" is <c>data.Length + 3</c> because the payload includes the
    /// cn=0x31, fn=0x50, m=0x30 selector bytes.
    /// </summary>
    public EscPosBuilder QrCode(string data, byte moduleSize = 6, byte errorCorrection = 49)
    {
        EnsureInit();

        if (string.IsNullOrWhiteSpace(data))
        {
            return this;
        }

        var payload = _encoding.GetBytes(data);
        var storeLength = payload.Length + 3;

        if (storeLength > 0xFFF0)
        {
            return this; // beyond printer memory — caller should shorten the URL
        }

        Raw(EscPosCommand.QrSelectModel2);
        Raw(EscPosCommand.QrModuleSize((byte)Math.Clamp(moduleSize, 1, 16)));
        Raw(EscPosCommand.QrErrorCorrection(errorCorrection));

        Raw(EscPosCommand.Gs, 0x28, 0x6B,
            (byte)(storeLength & 0xFF), (byte)((storeLength >> 8) & 0xFF),
            0x31, 0x50, 0x30);
        Raw(payload);

        // Print the symbol just stored.
        Raw(EscPosCommand.Gs, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30);
        return this;
    }

    /// <summary>
    /// Prints a pre-dithered bitmap as an ESC/POS raster image (GS v 0). The caller supplies
    /// 1-bit-per-pixel data, MSB-first, row padded to a whole byte — see <c>LogoRasterizer</c>.
    /// </summary>
    public EscPosBuilder RasterImage(int widthBytes, int heightDots, byte[] data, byte mode = 0)
    {
        EnsureInit();

        if (widthBytes <= 0 || heightDots <= 0 || data.Length < widthBytes * heightDots)
        {
            return this;
        }

        Raw(EscPosCommand.RasterHeader(widthBytes, heightDots, mode));
        Raw(data.AsSpan(0, widthBytes * heightDots));
        return this;
    }

    /// <summary>
    /// Drawer kick. Epson spec: <c>ESC p m t1 t2</c>. Pin 2 is wired to the drawer in ~95 % of
    /// installs; pin 5 is used for the second drawer on dual-drawer setups.
    /// </summary>
    public EscPosBuilder OpenDrawer(byte pin = 0, byte onTime = 25, byte offTime = 250)
    {
        EnsureInit();
        return Raw(EscPosCommand.DrawerKick(pin, onTime, offTime));
    }

    /// <summary>Adds the raw configured pulse string, e.g. "ESC p 0 25 250".</summary>
    public EscPosBuilder OpenDrawer(string command)
    {
        EnsureInit();
        return Raw(DrawerCommandParser.Parse(command));
    }

    // ── Job control ──────────────────────────────────────────────────────────────────────────

    public EscPosBuilder Feed(int lines = 1)
    {
        EnsureInit();
        for (var i = 0; i < Math.Clamp(lines, 0, 20); i++)
        {
            Raw(EscPosCommand.Lf);
        }
        return this;
    }

    public EscPosBuilder Cut(bool partialWithFeed = true)
    {
        EnsureInit();
        Raw(EscPosCommand.Feed(4)); // clear the cutter blade
        return Raw(EscPosCommand.Cut(partialWithFeed));
    }

    public byte[] Build()
    {
        EnsureInit();
        return _buffer.ToArray();
    }

    /// <summary>Diagnostics helper: renders the byte stream as an escaped string for the log.</summary>
    public static string Describe(ReadOnlySpan<byte> payload)
    {
        var sb = new StringBuilder(Math.Min(payload.Length * 4, 4096));
        foreach (var b in payload)
        {
            switch (b)
            {
                case EscPosCommand.Esc: sb.Append("<ESC>"); break;
                case EscPosCommand.Gs: sb.Append("<GS>"); break;
                case EscPosCommand.Lf: sb.Append("<LF>\n"); break;
                case >= 0x20 and < 0x7F: sb.Append((char)b); break;
                default: sb.Append('<').Append(b.ToString("X2")).Append('>'); break;
            }
        }
        return sb.ToString();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Greedy word wrap that also hard-splits tokens longer than the roll width.</summary>
    public static IEnumerable<string> WrapLines(string text, int width)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        foreach (var paragraph in text.Split('\n'))
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = new StringBuilder();

            foreach (var word in words)
            {
                var candidateLength = current.Length == 0 ? word.Length : current.Length + 1 + word.Length;

                if (candidateLength <= width)
                {
                    if (current.Length > 0)
                    {
                        current.Append(' ');
                    }
                    current.Append(word);
                    continue;
                }

                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                // A single token wider than the roll (long SKU, URL) must be hard-split.
                var remaining = word;
                while (remaining.Length > width)
                {
                    yield return remaining[..width];
                    remaining = remaining[width..];
                }

                current.Append(remaining);
            }

            yield return current.ToString();
        }
    }
}
