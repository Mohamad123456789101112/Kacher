using System.Text;

namespace Kacher.Pos.Services.Hardware.EscPos;

/// <summary>
/// Maps an ESC/POS code-page index (<c>ESC t n</c>) to the matching .NET encoding so Arabic,
/// Greek and accented Latin product names print as glyphs instead of mojibake.
///
/// .NET Core ships only UTF-8/UTF-16/ASCII; single-byte code pages live in
/// <c>System.Text.Encoding.CodePages</c> and must be registered once at startup.
/// </summary>
public static class EscPosEncoding
{
    private static readonly Dictionary<byte, (int CodePage, string Name)> Map = new()
    {
        [0] = (437, "CP437 — USA/Standard Europe"),
        [2] = (850, "CP850 — Multilingual Latin I"),
        [3] = (860, "CP860 — Portuguese"),
        [4] = (863, "CP863 — Canadian French"),
        [5] = (865, "CP865 — Nordic"),
        [16] = (1252, "WPC1252 — Latin I"),
        [17] = (866, "CP866 — Cyrillic"),
        [18] = (852, "CP852 — Latin II"),
        [19] = (858, "CP858 — Euro"),
        [21] = (862, "CP862 — Hebrew"),
        [22] = (864, "CP864 — Arabic"),
        [23] = (737, "CP737 — Greek"),
        [24] = (1257, "WPC1257 — Baltic"),
        [25] = (1256, "WPC1256 — Arabic (Windows)"),
        [26] = (1255, "WPC1255 — Hebrew (Windows)"),
        [27] = (1254, "WPC1254 — Turkish (Windows)"),
        [28] = (1258, "WPC1258 — Vietnamese")
    };

    private static bool _providerRegistered;

    public static IReadOnlyCollection<(byte Index, string Name)> Available
    {
        get
        {
            EnsureProvider();
            return Map.Select(kv => (kv.Key, kv.Value.Name)).OrderBy(x => x.Key).ToList();
        }
    }

    public static Encoding ForIndex(byte index)
    {
        EnsureProvider();
        var codePage = Map.TryGetValue(index, out var info) ? info.CodePage : 1252;
        return Encoding.GetEncoding(codePage);
    }

    public static string NameFor(byte index) => Map.TryGetValue(index, out var info) ? info.Name : $"Unknown ({index})";

    public static void EnsureProvider()
    {
        if (_providerRegistered)
        {
            return;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _providerRegistered = true;
    }
}
