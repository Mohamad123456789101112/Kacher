using System.Globalization;
using System.Text;

namespace Kacher.Pos.Services.Pdf;

/// <summary>
/// Minimal write-only PDF 1.4 generator: pages, Helvetica text, rules and filled rectangles.
/// </summary>
/// <remarks>
/// <para>
/// A POS terminal must not depend on Office, a browser or a 3 MB PDF library to print the day's
/// takings. The subset used here — one page tree, two base-14 fonts, uncompressed content streams —
/// is roughly 60 lines of PDF syntax and is accepted by every reader and by Windows' own
/// "Microsoft Print to PDF" queue.
/// </para>
/// <para>
/// Coordinates are given from the <b>top-left</b> of the page (natural for laying out a receipt) and
/// flipped to PDF's bottom-left origin when the stream is emitted.
/// </para>
/// </remarks>
public sealed class SimplePdfWriter
{
    private const double Margin = 36;

    private readonly List<PdfPage> _pages = new();
    private readonly double _pageWidth;
    private readonly double _pageHeight;

    public SimplePdfWriter(double pageWidth, double pageHeight)
    {
        _pageWidth = pageWidth;
        _pageHeight = pageHeight;
        AddPage();
    }

    public int PageCount => _pages.Count;

    public double Width => _pageWidth;

    public double Height => _pageHeight;

    public double LeftMargin => Margin;

    public double RightEdge => _pageWidth - Margin;

    public double BottomEdge => _pageHeight - Margin;

    public SimplePdfWriter AddPage()
    {
        _pages.Add(new PdfPage());
        return this;
    }

    /// <summary>Writes a single line of text with its baseline at <paramref name="yFromTop"/>.</summary>
    public SimplePdfWriter Text(double x, double yFromTop, string? text, double size = 9, bool bold = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return this;
        }

        var font = bold ? "/F2" : "/F1";
        var y = _pageHeight - yFromTop;

        _pages[^1].Content.Append(string.Create(CultureInfo.InvariantCulture, $"BT {font} {size:0.##} Tf 1 0 0 1 {x:0.##} {y:0.##} Tm "));
        _pages[^1].Content.Append('(').Append(Escape(text)).Append(") Tj ET\n");
        return this;
    }

    /// <summary>Text aligned to the right margin — used for money columns.</summary>
    public SimplePdfWriter TextRight(string? text, double yFromTop, double size = 9, bool bold = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return this;
        }

        // Helvetica's average advance is 0.5 em for digits and 0.55 for upper case; 0.5 is accurate
        // enough for column alignment of numeric strings.
        var estimatedWidth = text.Length * size * 0.5;
        return Text(Math.Max(LeftMargin, RightEdge - estimatedWidth), yFromTop, text, size, bold);
    }

    /// <summary>Centres text horizontally using the same advance-width estimate.</summary>
    public SimplePdfWriter TextCentre(string? text, double yFromTop, double size = 9, bool bold = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return this;
        }

        var estimatedWidth = text.Length * size * 0.5;
        return Text((_pageWidth - estimatedWidth) / 2, yFromTop, text, size, bold);
    }

    /// <summary>Horizontal rule from the left margin to the right margin.</summary>
    public SimplePdfWriter Rule(double yFromTop, double thickness = 0.5)
    {
        var y = _pageHeight - yFromTop;
        _pages[^1].Content.Append(string.Create(CultureInfo.InvariantCulture,
            $"{thickness:0.##} w {LeftMargin:0.##} {y:0.##} m {RightEdge:0.##} {y:0.##} l S\n"));
        return this;
    }

    /// <summary>Filled rectangle, e.g. the Z-report's total band.</summary>
    public SimplePdfWriter Rectangle(double x, double yFromTop, double width, double height, double grey = 0.9)
    {
        var y = _pageHeight - yFromTop - height;
        _pages[^1].Content.Append(string.Create(CultureInfo.InvariantCulture,
            $"{grey:0.###} g {x:0.##} {y:0.##} {width:0.##} {height:0.##} re f 0 g\n"));
        return this;
    }

    /// <summary>
    /// Serialises the document. Object layout: 1 catalog, 2 page tree, 3/4 fonts, then one page
    /// object and one content stream per page, so offsets can be computed in a single pass.
    /// </summary>
    public byte[] Build(string title = "Kacher POS document")
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            string.Empty, // page tree, filled in once the object numbers are known
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"
        };

        var pageObjectNumbers = new List<int>(_pages.Count);

        // Page objects start at 5 and streams alternate after them.
        var nextNumber = 5;

        foreach (var page in _pages)
        {
            pageObjectNumbers.Add(nextNumber);
            nextNumber += 2;
        }

        var kids = string.Join(' ', pageObjectNumbers.Select(n => $"{n} 0 R"));
        objects[1] = $"<< /Type /Pages /Kids [{kids}] /Count {_pages.Count} >>";

        for (var index = 0; index < _pages.Count; index++)
        {
            var page = _pages[index];
            var contentStream = page.Content.ToString();
            var streamObjectNumber = pageObjectNumbers[index] + 1;

            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {_pageWidth:0.##} {_pageHeight:0.##}] " +
                $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {streamObjectNumber} 0 R >>");

            objects.Add($"<< /Length {Encoding.Latin1.GetByteCount(contentStream)} >>\nstream\n{contentStream}endstream");
        }

        // ── Assemble: header, bodies (recording byte offsets), xref, trailer ───────────────────
        using var buffer = new MemoryStream(16 * 1024);
        using var writer = new StreamWriter(buffer, Encoding.Latin1, 16 * 1024, leaveOpen: true);

        writer.Write("%PDF-1.4\n");
        writer.Write("%\u00E2\u00E3\u00CF\u00D3\n"); // binary marker so tools treat the file as binary
        writer.Flush();

        var offsets = new long[objects.Count + 1];

        for (var i = 0; i < objects.Count; i++)
        {
            var number = i + 1;
            offsets[number] = buffer.Position;

            writer.Write(string.Create(CultureInfo.InvariantCulture, $"{number} 0 obj\n{objects[i]}\nendobj\n"));
            writer.Flush();
        }

        var xrefPosition = buffer.Position;

        writer.Write(string.Create(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n"));
        writer.Write("0000000000 65535 f \n");

        for (var number = 1; number <= objects.Count; number++)
        {
            writer.Write(string.Create(CultureInfo.InvariantCulture, $"{offsets[number]:D10} 00000 n \n"));
        }

        writer.Write(string.Create(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info << /Title ({Escape(title)}) " +
            $"/Producer (Kacher POS) >> >>\nstartxref\n{xrefPosition}\n%%EOF\n"));

        writer.Flush();
        return buffer.ToArray();
    }

    /// <summary>
    /// Escapes a PDF literal string and drops anything outside WinAnsi — a receipt is not the place
    /// for an emoji, and a stray parenthesis would corrupt the content stream.
    /// </summary>
    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);

        foreach (var ch in value)
        {
            switch (ch)
            {
                case '(':
                    sb.Append("\\(");
                    break;
                case ')':
                    sb.Append("\\)");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\r':
                case '\n':
                    sb.Append(' ');
                    break;
                default:
                    sb.Append(ch is >= ' ' and <= '~' or >= '\u00A1' and <= '\u00FF' ? ch : '?');
                    break;
            }
        }

        return sb.ToString();
    }

    private sealed class PdfPage
    {
        public StringBuilder Content { get; } = new(2048);
    }
}
