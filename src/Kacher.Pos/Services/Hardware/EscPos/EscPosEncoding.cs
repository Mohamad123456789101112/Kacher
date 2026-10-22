using System.Text;

namespace Kacher.Pos.Services.Hardware.EscPos;

/// <summary>
/// Maps a printer code-page index (as stored in <c>HardwareConfig.CodePage</c>) to a .NET
/// <see cref="Encoding"/>, and guarantees that unrenderable characters degrade to '?' instead of
/// corrupting the byte stream.
/// </summary>
/// <remarks>
/// .NET 8 only ships UTF-8/UTF-16/ASCII by default; the legacy single-byte code pages used by
/// ESC/POS printers (CP437, CP850, Windows-1252, CP864, Windows-1256) require
/// <see cref="Encoding.RegisterProvider"/> with <c>System.Text.Encoding.CodePages</c>.
/// </remarks>
public static class EscPosEncoding
{
    private static readonly object Gate = new();
    private static bool _providerRegistered;

    /// <summary>Printer code page index (ESC t n) → Windows code page number.</summary>
    private static readonly IReadOnlyDictionary<byte, int> PrinterCodePages = new Dictionary<byte, int>
    {
        [0] = 437,   // USA / Standard Europe
        [2] = 850,   // Multilingual Latin 1
        [3] = 860,   // Portuguese
        [4] = 863,   // Canadian French
        [5] = 865,   // Nordic
        [16] = 1252, // WPC1252 (the default on most Epson units sold in EMEA)
        [17] = 866,  // Cyrillic
        [18] = 852,  // Latin 2
        [19] = 858,  // Latin 1 + euro
        [22] = 864,  // Arabic
        [23] = 862,  // Hebrew
        [24] = 1256, // WPC1256 (Arabic, Windows)
        [25] = 1257, // Windows Baltic
        [255] = 437
    };

    /// <summary>Code pages offered in the hardware settings drop-down, in display order.</summary>
    public static IReadOnlyList<CodePageInfo> Available { get; } = new List<CodePageInfo>
    {
        new(16, "WPC1252 — Western Europe (default)"),
        new(0, "CP437 — USA / Standard Europe"),
        new(2, "CP850 — Multilingual Latin 1"),
        new(19, "CP858 — Latin 1 with euro"),
        new(22, "CP864 — Arabic (DOS)"),
        new(24, "WPC1256 — Arabic (Windows)"),
        new(23, "CP862 — Hebrew"),
        new(17, "CP866 — Cyrillic"),
        new(18, "CP852 — Latin 2"),
        new(25, "WPC1257 — Baltic")
    };

    /// <summary>Registers the legacy code-page provider once per process.</summary>
    public static void EnsureProvider()
    {
        if (_providerRegistered)
        {
            return;
        }

        lock (Gate)
        {
            if (_providerRegistered)
            {
                return;
            }

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _providerRegistered = true;
        }
    }

    /// <summary>
    /// Returns an encoder for the printer code page. Replacements are written as '?' so a stray
    /// emoji in a product name can never desynchronise the ESC/POS parser.
    /// </summary>
    public static Encoding ForPrinter(byte printerCodePage)
    {
        EnsureProvider();

        var windowsCodePage = PrinterCodePages.TryGetValue(printerCodePage, out var mapped) ? mapped : 1252;

        try
        {
            return Encoding.GetEncoding(
                windowsCodePage,
                EncoderFallback.ReplacementFallback,
                DecoderFallback.ReplacementFallback);
        }
        catch (ArgumentException)
        {
            // The provider is missing a rarely installed code page — fall back to Latin-1.
            return Encoding.GetEncoding(
                1252,
                EncoderFallback.ReplacementFallback,
                DecoderFallback.ReplacementFallback);
        }
    }

    /// <summary>Columns available on the roll in Font A (12×24) at 203 dpi.</summary>
    public static int ColumnsFor(Models.PaperWidth width) => width == Models.PaperWidth.Mm58 ? 32 : 48;

    /// <summary>Printable dot width used for the logo raster.</summary>
    public static int DotsFor(Models.PaperWidth width) => width == Models.PaperWidth.Mm58 ? 288 : 384;
}

/// <summary>One entry of the printer code-page list (index sent with ESC t).</summary>
public sealed record CodePageInfo(byte Index, string Name);
