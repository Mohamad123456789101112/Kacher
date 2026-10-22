using System.Globalization;
using System.Text;

namespace Kacher.Pos.Services.Hardware.EscPos;

/// <summary>
/// Turns the operator-editable <c>OpenDrawerCommand</c> string into bytes.
///
/// Three notations are accepted so field technicians can paste whatever the vendor documentation
/// says without touching code:
///   1. Mnemonic  : "ESC p 0 25 250"   (ESC/GS/FS/DLE prefixes, ASCII mnemonics, decimal params)
///   2. Prefixed  : "0x1B,0x70,0x00,0x19,0xFA"
///   3. Hex blob  : "1B 70 00 19 FA" / "1B700019FA"
/// </summary>
public static class DrawerCommandParser
{
    /// <summary>Epson factory default: pin 2, 50 ms on, 500 ms off — i.e. <c>ESC p 0 25 250</c>.</summary>
    public static byte[] DefaultPulse => new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA };

    public static byte[] Parse(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return DefaultPulse;
        }

        var trimmed = command.Trim();

        // ── Notation 3: hex blob ──────────────────────────────────────────────────────────────
        if (LooksLikeHexBlob(trimmed))
        {
            var compact = new string(trimmed.Where(Uri.IsHexDigit).ToArray());
            if (compact.Length % 2 != 0)
            {
                return DefaultPulse;
            }

            var bytes = new byte[compact.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = byte.Parse(compact.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            return bytes;
        }

        // ── Notations 1 & 2: token stream ─────────────────────────────────────────────────────
        // Commas are separators in notation 2, whitespace in notation 1; normalise both.
        var tokens = trimmed
            .Replace(',', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var output = new List<byte>(tokens.Length);

        foreach (var token in tokens)
        {
            switch (token.ToUpperInvariant())
            {
                case "ESC": output.Add(EscPosCommand.Esc); continue;
                case "GS": output.Add(EscPosCommand.Gs); continue;
                case "FS": output.Add(EscPosCommand.Fs); continue;
                case "DLE": output.Add(0x10); continue;
                case "LF": output.Add(EscPosCommand.Lf); continue;
                case "CR": output.Add(EscPosCommand.Cr); continue;
                case "HT": output.Add(EscPosCommand.Ht); continue;
            }

            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                byte.TryParse(token.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                output.Add(hex);
                continue;
            }

            if (byte.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                output.Add(number);
                continue;
            }

            // Remaining tokens are ASCII mnemonics such as the "p" in ESC p.
            foreach (var ch in token)
            {
                output.Add((byte)ch);
            }
        }

        return output.Count == 0 ? DefaultPulse : output.ToArray();
    }

    /// <summary>Human-readable rendering of the configured pulse for the settings screen.</summary>
    public static string Explain(string? command)
    {
        var bytes = Parse(command);
        var sb = new StringBuilder();

        foreach (var b in bytes)
        {
            if (b >= 0x20 && b < 0x7F)
            {
                sb.Append((char)b).Append(' ');
            }
            else
            {
                sb.Append(b.ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static bool LooksLikeHexBlob(string value)
    {
        // A hex blob contains no letters outside A-F and at least one digit pair.
        var hexCount = 0;
        foreach (var ch in value)
        {
            if (ch is ' ' or '-' or ':')
            {
                continue;
            }

            if (!Uri.IsHexDigit(ch))
            {
                return false;
            }

            hexCount++;
        }

        return hexCount >= 4 && hexCount % 2 == 0;
    }
}
