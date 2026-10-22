using System.Globalization;

namespace Kacher.Pos.Services.Hardware.EscPos;

/// <summary>The three coordinates of an ESC/POS drawer kick: kick pin and the two pulse widths.</summary>
public sealed record DrawerPulse(byte Pin, byte OnTime, byte OffTime)
{
    /// <summary>Standard Epson drive for a 24 V solenoid: pin 2, 50 ms on, 500 ms off.</summary>
    public static DrawerPulse Default { get; } = new(0, 25, 250);

    /// <summary>Pin 5 is used by some NCR/Star drawers wired to the secondary connector.</summary>
    public static DrawerPulse Pin5 { get; } = new(1, 25, 250);

    public byte[] ToBytes() => [EscPosCommand.Esc, 0x70, Pin, OnTime, OffTime];

    public string ToNotation() =>
        string.Create(CultureInfo.InvariantCulture, $"ESC p {Pin} {OnTime} {OffTime}");
}

/// <summary>
/// Parses the free-text <c>HardwareConfig.OpenDrawerCommand</c> field into a
/// <see cref="DrawerPulse"/>. Accepts the human notation ("ESC p 0 25 250"), raw hex
/// ("1B 70 00 19 FA", "0x1B 0x70 …") and the "GS" variants some vendors document.
/// </summary>
public static class DrawerCommandParser
{
    /// <summary>
    /// Parses <paramref name="command"/>. Returns <c>false</c> with a reason instead of throwing so
    /// the settings screen can show a validation message while the cashier keeps working.
    /// </summary>
    public static bool TryParse(string? command, out DrawerPulse pulse, out string? error)
    {
        pulse = DrawerPulse.Default;
        error = null;

        if (string.IsNullOrWhiteSpace(command))
        {
            error = "Drawer command is empty; the default ESC p 0 25 250 will be used.";
            return false;
        }

        var trimmed = command.Trim();

        // A full mnemonic such as "ESC p 0 25 250" — the mnemonic carries the 0x1B prefix.
        if (trimmed.StartsWith("ESC", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed[3..].Trim();
            var tokens = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 0 || !tokens[0].Equals("p", StringComparison.OrdinalIgnoreCase))
            {
                error = "Expected the ESC p n m t sequence, e.g. ESC p 0 25 250.";
                return false;
            }

            var numbers = new List<byte>();
            foreach (var token in tokens.Skip(1))
            {
                if (!byte.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    error = $"'{token}' is not a byte value (0-255).";
                    return false;
                }

                numbers.Add(value);
            }

            if (numbers.Count != 3)
            {
                error = "ESC p needs three parameters: pin, on-time and off-time.";
                return false;
            }

            pulse = new DrawerPulse(numbers[0], numbers[1], numbers[2]);
            return true;
        }

        // Raw hex, with or without 0x prefixes and any separator.
        var cleaned = trimmed
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(",", " ")
            .Replace("-", " ");

        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new List<byte>(parts.Length);

        foreach (var part in parts)
        {
            if (!byte.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                error = $"'{part}' is not a hex byte.";
                return false;
            }

            bytes.Add(value);
        }

        if (bytes.Count < 3)
        {
            error = "A drawer kick needs at least three bytes (ESC p …).";
            return false;
        }

        // Accept the canonical 5-byte form; tolerate a missing terminator by padding defaults.
        if (bytes[0] == EscPosCommand.Esc && bytes[1] == 0x70)
        {
            pulse = new DrawerPulse(
                bytes.Count > 2 ? bytes[2] : (byte)0,
                bytes.Count > 3 ? bytes[3] : (byte)25,
                bytes.Count > 4 ? bytes[4] : (byte)250);
            return true;
        }

        error = "Byte sequence does not start with ESC p (1B 70). Using the default pulse.";
        return false;
    }

    /// <summary>
    /// Human-friendly rendering of a drawer command, e.g.
    /// <c>"ESC p 0 25 250 → 1B 70 00 19 FA (pin 2, 50 ms pulse)"</c>. Shown live as the operator
    /// types in the hardware screen so a typo is obvious before a sale is attempted.
    /// </summary>
    public static string Explain(string? command)
    {
        if (!TryParse(command, out var pulse, out var error))
        {
            return error is null ? "Invalid drawer command." : $"Invalid — {error}";
        }

        var bytes = string.Join(' ', pulse.ToBytes().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        var pin = pulse.Pin == 0 ? "pin 2" : "pin 5";
        var onMs = pulse.OnTime * 2;

        return $"{pulse.ToNotation()} → {bytes} ({pin}, {onMs} ms pulse)";
    }

    /// <summary>Parses or silently returns the default — used on the print path.</summary>
    public static DrawerPulse ParseOrDefault(string? command) => TryParse(command, out var pulse, out _) ? pulse : DrawerPulse.Default;
}
