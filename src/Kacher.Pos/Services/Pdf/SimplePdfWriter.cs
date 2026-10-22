using System.Globalization;
using System.IO;
using System.Text;

namespace Kacher.Pos.Services.Pdf;

public sealed record PdfTextLine(string Text, double X, double Y, double FontSize, bool Bold = false);

public sealed record PdfPage(double Width, double Height, IReadOnlyList<PdfTextLine> Lines);

/// <summary>
/// A dependency-free PDF 1.4 writer specialised for receipts and tabular reports.
///
/// Why not a third-party PDF library? A POS terminal should ship as few binaries as possible, and
/// the requirement is only "printable page with Helvetica text" — no images, no reflow. The writer
/// emits the six objects a viewer needs (Catalog, Pages, two fonts, Page, Contents) plus a correct
/// xref table, which is enough for Edge, Chrome, Acrobat and every thermal-printer PDF spooler.
///
/// Usage is intentionally low-level: caller lays out lines in PDF user space (points, origin
/// bottom-left) and this type handles escaping, encoding and cross-references.
/// </summary>
public static class SimplePdfWriter
{
    public static async Task WriteAsync(string path, IReadOnlyList<PdfPage> pages, CancellationToken ct = default)
    {
        if (pages.Count == 0)
        {
            pages = new[] { new PdfPage(595, 842, Array.Empty<PdfTextLine>()) };
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        await using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: false);

        var offsets = new List<long> { 0 };

        // Header. The binary comment marks the file as containing binary data.
        await WriteAsync(writer, "%PDF-1.4\n").ConfigureAwait(false);
        await writer.BaseStream.WriteAsync(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, ct).ConfigureAwait(false);

        // Object numbering: 1 Catalog, 2 Pages, 3 Helvetica, 4 Helvetica-Bold, then page/content pairs.
        const int firstPageObject = 5;

        WriteObject(writer, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>");

        var kids = new StringBuilder();
        for (var i = 0; i < pages.Count; i++)
        {
            kids.Append(firstPageObject + (i * 2)).Append(" 0 R ");
        }

        WriteObject(writer, offsets, 2, $"<< /Type /Pages /Kids [ {kids.ToString().Trim()} ] /Count {pages.Count} >>");
        WriteObject(writer, offsets, 3,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        WriteObject(writer, offsets, 4,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");

        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var pageObject = firstPageObject + (i * 2);
            var contentObject = pageObject + 1;

            var content = BuildContent(page);
            var contentBytes = Encoding.ASCII.GetBytes(content);

            WriteObject(writer, offsets, pageObject,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Format(page.Width)} {Format(page.Height)}] " +
                $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentObject} 0 R >>");

            // Stream objects are written manually because they carry a Length and raw bytes.
            offsets.Add(writer.BaseStream.Position);
            await WriteAsync(writer, $"{contentObject} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n").ConfigureAwait(false);
            await writer.BaseStream.WriteAsync(contentBytes, ct).ConfigureAwait(false);
            await WriteAsync(writer, "\nendstream\nendobj\n").ConfigureAwait(false);
        }

        // ── Cross-reference table ────────────────────────────────────────────────────────────
        var xrefOffset = writer.BaseStream.Position;
        var objectCount = offsets.Count; // object 0 is the free-list head

        await WriteAsync(writer, $"xref\n0 {objectCount}\n").ConfigureAwait(false);
        await WriteAsync(writer, "0000000000 65535 f \n").ConfigureAwait(false);

        foreach (var offset in offsets.Skip(1))
        {
            await WriteAsync(writer, $"{offset:D10} 00000 n \n").ConfigureAwait(false);
        }

        await WriteAsync(writer,
            $"trailer\n<< /Size {objectCount} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n").ConfigureAwait(false);

        await writer.FlushAsync().ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void WriteObject(BinaryWriter writer, List<long> offsets, int id, string body)
    {
        // offsets[0] is reserved for the free object; pad so index == object id.
        while (offsets.Count < id)
        {
            offsets.Add(0);
        }

        offsets.Add(writer.BaseStream.Position);
        Write(writer, $"{id} 0 obj\n{body}\nendobj\n");
    }

    private static string BuildContent(PdfPage page)
    {
        var sb = new StringBuilder();
        sb.Append("BT\n");

        foreach (var line in page.Lines)
        {
            if (string.IsNullOrEmpty(line.Text))
            {
                continue;
            }

            var font = line.Bold ? "/F2" : "/F1";
            sb.Append(Format(line.FontSize)).Append(" Tf ")
              .Append("1 0 0 1 ").Append(Format(line.X)).Append(' ').Append(Format(line.Y)).Append(" Tm ")
              .Append('(').Append(Escape(line.Text)).Append(") Tj\n");
        }

        sb.Append("ET\n");
        return sb.ToString();
    }

    /// <summary>
    /// Escapes the three characters that are special inside a PDF literal string and replaces
    /// anything outside WinAnsi with '?' — the built-in Helvetica cannot render it anyway.
    /// </summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '(': sb.Append(@"\("); break;
                case ')': sb.Append(@"\)"); break;
                case '\\': sb.Append(@"\\"); break;
                case '\r': break;
                case '\n': sb.Append(' '); break;
                default:
                    sb.Append(ch is >= ' ' and <= '~' ? ch : '?');
                    break;
            }
        }

        return sb.ToString();
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void Write(BinaryWriter writer, string text) =>
        writer.Write(Encoding.ASCII.GetBytes(text));

    private static Task WriteAsync(BinaryWriter writer, string text) =>
        writer.BaseStream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();
}
