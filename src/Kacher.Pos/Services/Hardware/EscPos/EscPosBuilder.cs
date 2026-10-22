using System.Text;

namespace Kacher.Pos.Services.Hardware.EscPos;

public enum TextAlignment : byte
{
    Left = 0,
    Centre = 1,
    Right = 2
}

/// <summary>
/// Fluent composer for ESC/POS receipt payloads. Callers describe the ticket top-to-bottom and the
/// builder appends the matching command bytes; nothing is sent until <see cref="ToArray"/>.
/// </summary>
/// <remarks>
/// Two design points matter for reliability on real hardware:
/// <list type="bullet">
/// <item>Text is encoded <b>once</b> with the printer's code page, so a character the printer cannot
/// render becomes '?' instead of shifting every subsequent byte.</item>
/// <item>Column helpers take the roll width into account (48 characters at 80 mm, 32 at 58 mm in
/// Font A) so the same receipt template works on both printer classes.</item>
/// </list>
/// </remarks>
public sealed class EscPosBuilder
{
    private readonly List<byte> _buffer = new(2048);
    private readonly Encoding _encoding;
    private readonly int _columns;

    public EscPosBuilder(Encoding encoding, int columns)
    {
        _encoding = encoding ?? throw new ArgumentNullException(nameof(encoding));
        _columns = Math.Max(24, columns);
    }

    public int Columns => _columns;

    public int Length => _buffer.Count;

    // ── Primitives ────────────────────────────────────────────────────────────────────────────

    public EscPosBuilder Raw(ReadOnlySpan<byte> bytes)
    {
        // List<byte>.AddRange takes IEnumerable<byte>, which a span is not, so copy element-wise.
        foreach (var b in bytes)
        {
            _buffer.Add(b);
        }

        return this;
    }

    /// <summary>Power-on reset plus the configured code page. Always the first command in a job.</summary>
    public EscPosBuilder Initialise(byte codePage)
    {
        Raw(EscPosCommand.Initialise);
        Raw(EscPosCommand.CodePage(codePage));
        return Align(TextAlignment.Left);
    }

    public EscPosBuilder Align(TextAlignment alignment) => Raw(EscPosCommand.Align((byte)alignment));

    public EscPosBuilder Bold(bool on = true) => Raw(EscPosCommand.Bold(on));

    public EscPosBuilder Underline(bool on = true) => Raw(EscPosCommand.Underline((byte)(on ? 1 : 0)));

    public EscPosBuilder Size(int width = 1, int height = 1) => Raw(EscPosCommand.TextSize(width, height));

    public EscPosBuilder Feed(int lines = 1) => Raw(EscPosCommand.Feed((byte)Math.Clamp(lines, 0, 255)));

    public EscPosBuilder Cut(bool partial = false) => Raw(EscPosCommand.Cut(partial));

    /// <summary>Text without a line break, encoded in the active code page.</summary>
    public EscPosBuilder Text(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return this;
        }

        _buffer.AddRange(_encoding.GetBytes(text));
        return this;
    }

    public EscPosBuilder Line(string? text = null)
    {
        Text(text);
        _buffer.Add(EscPosCommand.Lf);
        return this;
    }

    /// <summary>A full-width separator drawn with a single character.</summary>
    public EscPosBuilder Rule(char glyph = '-') => Line(new string(glyph, _columns));

    /// <summary>Pads to <paramref name="width"/>; never truncates a wider string.</summary>
    public static string Pad(string text, int width)
        => text.Length >= width ? text : text.PadRight(width);

    /// <summary>Left-aligned label and right-aligned value on the same line.</summary>
    public EscPosBuilder TwoColumn(string left, string right, char filler = ' ')
    {
        // Reserve one gutter column so the two halves never collide when the printer's font is
        // slightly wider than the nominal character width.
        var gap = _columns - left.Length - right.Length - 1;

        if (gap < 1)
        {
            // Too long for one line: fall back to two lines rather than clipping money.
            Line(left);
            return Line(right.PadLeft(_columns));
        }

        return Line(left + new string(filler, gap) + right);
    }

    /// <summary>Item / quantity / amount row used by the receipt table.</summary>
    public EscPosBuilder ThreeColumn(string first, string middle, string last)
    {
        var moneyWidth = Math.Max(8, _columns / 4);
        var middleWidth = Math.Max(6, _columns / 5);
        var firstWidth = Math.Max(8, _columns - moneyWidth - middleWidth);

        var a = first.Length > firstWidth ? first[..firstWidth] : Pad(first, firstWidth);
        var b = Pad(middle, middleWidth);
        var c = last.PadLeft(moneyWidth);

        return Line(a + b + c);
    }

    /// <summary>Word-wraps at the roll width, preserving explicit line breaks.</summary>
    public EscPosBuilder Paragraph(string? text, string indent = "")
    {
        foreach (var line in Wrap(text, _columns - indent.Length))
        {
            Line(indent + line);
        }

        return this;
    }

    public static IEnumerable<string> Wrap(string? text, int width)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield return string.Empty;
            yield break;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var words = rawLine.TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = new StringBuilder();

            foreach (var word in words)
            {
                if (current.Length == 0)
                {
                    current.Append(word);
                }
                else if (current.Length + 1 + word.Length <= width)
                {
                    current.Append(' ').Append(word);
                }
                else
                {
                    yield return current.ToString();
                    current.Clear().Append(word);
                }

                // A single token longer than the line has to be chopped.
                while (current.Length > width)
                {
                    yield return current.ToString(0, width);
                    current.Remove(0, width);
                }
            }

            yield return current.Length == 0 ? string.Empty : current.ToString();
        }
    }

    // ── Symbols ───────────────────────────────────────────────────────────────────────────────

    public EscPosBuilder Barcode(string data)
    {
        return string.IsNullOrWhiteSpace(data) ? this : Raw(EscPosCommand.Code128(data));
    }

    public EscPosBuilder QrCode(string data, byte moduleSize = 6)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return this;
        }

        return Raw(EscPosCommand.QrConfigure(moduleSize))
               .Raw(EscPosCommand.QrStore(data))
               .Raw(EscPosCommand.QrPrint)
               .Feed();
    }

    /// <summary>
    /// Prints a logo. <paramref name="raster"/> must already be 1-bit row-major data produced by
    /// <c>LogoRasterizer</c> for the right dot width (384 at 80 mm, 288 at 58 mm).
    /// </summary>
    public EscPosBuilder Logo(int widthDots, int heightDots, byte[] raster)
    {
        return raster.Length == 0 ? this : Raw(EscPosCommand.RasterImage(widthDots, heightDots, raster)).Feed();
    }

    public EscPosBuilder OpenDrawer(byte pin = 0, byte onTime = 25, byte offTime = 250)
        => Raw(EscPosCommand.OpenDrawer(pin, onTime, offTime));

    public byte[] ToArray() => _buffer.ToArray();
}
