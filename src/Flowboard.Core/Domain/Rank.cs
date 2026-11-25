using System.Text;

namespace Flowboard.Core.Domain;

/// <summary>
/// Fractional ordering keys: a new key can always be generated between two existing ones, so moving a
/// card rewrites exactly one row instead of renumbering the whole column. Keys are strings over
/// 0-9a-z, compared ordinally, and never end in '0' (so there is always room below them).
/// </summary>
/// <remarks>Port of the "fractional indexing" midpoint algorithm (digits only, no integer part).</remarks>
public static class Rank
{
    private const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>Keys longer than this suggest the column should be renumbered.</summary>
    public const int RebalanceLength = 24;

    /// <summary>A key that sorts strictly between <paramref name="before"/> and <paramref name="after"/>.</summary>
    /// <param name="before">Key of the item above, or null for the top of the list.</param>
    /// <param name="after">Key of the item below, or null for the bottom of the list.</param>
    public static string Between(string? before, string? after)
    {
        before ??= string.Empty;
        Validate(before);
        if (after is not null)
        {
            Validate(after);
            if (string.CompareOrdinal(before, after) >= 0)
            {
                throw new ArgumentException($"'{before}' must sort before '{after}'.", nameof(after));
            }
        }

        return Midpoint(before, after);
    }

    /// <summary><paramref name="count"/> evenly spaced, short keys in ascending order (for new columns or renumbering).</summary>
    public static IReadOnlyList<string> Spread(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        // Two digits give 36*36 slots; spread the keys evenly across them (trailing zeros dropped:
        // "10" becomes "1", which still sorts between "0z" and "11").
        const int slots = 36 * 36;
        if (count >= slots)
        {
            return SpreadLong(count);
        }

        var keys = new List<string>(count);
        for (var i = 1; i <= count; i++)
        {
            var value = (int)((long)i * slots / (count + 1));
            keys.Add(new StringBuilder().Append(Digits[value / 36]).Append(Digits[value % 36]).ToString().TrimEnd('0'));
        }

        return keys;
    }

    private static List<string> SpreadLong(int count)
    {
        var keys = new List<string>(count);
        string? previous = null;
        for (var i = 0; i < count; i++)
        {
            previous = Between(previous, null);
            keys.Add(previous);
        }

        return keys;
    }

    private static string Midpoint(string a, string? b)
    {
        if (b is not null)
        {
            // Skip the common prefix (treating a missing digit in a as '0').
            var n = 0;
            while (n < b.Length && (n < a.Length ? a[n] : '0') == b[n])
            {
                n++;
            }

            if (n > 0)
            {
                return string.Concat(b.AsSpan(0, n), Midpoint(n < a.Length ? a[n..] : string.Empty, b[n..]));
            }
        }

        var digitA = a.Length > 0 ? Digits.IndexOf(a[0], StringComparison.Ordinal) : 0;
        var digitB = b is not null ? Digits.IndexOf(b[0], StringComparison.Ordinal) : Digits.Length;
        if (digitB - digitA > 1)
        {
            return Digits[(int)Math.Round(0.5 * (digitA + digitB), MidpointRounding.AwayFromZero)].ToString();
        }

        // The first digits are neighbours: keep a's digit and go one level deeper.
        if (b is not null && b.Length > 1)
        {
            return b[..1];
        }

        return Digits[digitA] + Midpoint(a.Length > 0 ? a[1..] : string.Empty, null);
    }

    private static void Validate(string key)
    {
        foreach (var c in key)
        {
            if (Digits.IndexOf(c, StringComparison.Ordinal) < 0)
            {
                throw new ArgumentException($"Invalid rank '{key}'.", nameof(key));
            }
        }

        if (key.Length > 0 && key[^1] == '0')
        {
            throw new ArgumentException($"Rank '{key}' must not end in '0'.", nameof(key));
        }
    }
}
