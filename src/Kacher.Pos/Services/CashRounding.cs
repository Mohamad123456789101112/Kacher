namespace Kacher.Pos.Services;

/// <summary>
/// Cash rounding helpers. Egyptian market practice (and most cash-heavy markets) rounds the
/// cash tender to the nearest 0.25 EGP so the drawer never needs small coin change.
/// Card payments are never rounded — the exact amount is charged.
/// </summary>
public static class CashRounding
{
    public const decimal DefaultIncrement = 0.25m;

    public static decimal RoundToNearest(decimal amount, decimal increment = DefaultIncrement)
    {
        if (increment <= 0)
        {
            return amount;
        }

        return Math.Round(amount / increment, MidpointRounding.AwayFromZero) * increment;
    }

    /// <summary>Rounds up to the next increment — used for "round up for charity" shortcuts.</summary>
    public static decimal RoundUp(decimal amount, decimal increment = DefaultIncrement)
    {
        if (increment <= 0)
        {
            return amount;
        }

        return Math.Ceiling(amount / increment) * increment;
    }

    /// <summary>Common banknote denominations offered as one-tap "exact cash" buttons.</summary>
    public static IReadOnlyList<decimal> SuggestedTenders(decimal total)
    {
        Span<decimal> notes = stackalloc decimal[] { 5, 10, 20, 50, 100, 200, 500 };
        var result = new List<decimal>(8) { total };

        foreach (var note in notes)
        {
            if (note > total)
            {
                result.Add(note);
            }
        }

        // Always offer the next round note so the cashier never types.
        var nextRound = (Math.Floor(total / 50) + 1) * 50;
        if (!result.Contains(nextRound))
        {
            result.Add(nextRound);
        }

        return result.Distinct().OrderBy(v => v).ToList();
    }
}
