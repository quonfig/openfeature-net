namespace Quonfig.OpenFeature.ServerProvider;

/// <summary>
/// Validates a stored duration's ISO 8601 text against the shared Quonfig duration grammar
/// (<c>integration-test-data/tests/duration/grammar.yaml</c>):
/// <code>^P(?:\d+D)?(?:T(?:\d+H)?(?:\d+M)?(?:\d+(?:\.\d+)?S)?)?$</code>
/// matched against the whole string with ASCII digits only, plus: at least one component, no
/// dangling <c>T</c>, a fraction only on <c>S</c> with at most 9 digits, total magnitude
/// &lt;= <c>P36500D</c>.
/// </summary>
/// <remarks>
/// A port of <c>Quonfig.Sdk</c>'s parser, which is internal: the provider needs only the yes/no
/// answer, and asking the SDK (a typed <c>GetDurationDetails</c> read) would evaluate the config a
/// second time and record a second evaluation in telemetry.
/// </remarks>
internal static class IsoDurationText
{
    /// <summary>Magnitude ceiling: P36500D in milliseconds.</summary>
    private const decimal MaxMillis = 36_500m * 86_400_000m;

    // A component value with more significant digits than this is far past the ceiling; rejecting
    // it up front keeps the decimal arithmetic from overflowing.
    private const int MaxSignificantDigits = 18;

    /// <summary>True when <paramref name="s"/> is a valid Quonfig duration.</summary>
    public static bool IsValid(string? s)
    {
        if (s is null || s.Length < 2 || s[0] != 'P')
        {
            return false;
        }

        int i = 1;
        int n = s.Length;
        decimal total = 0m;
        int components = 0;

        // Date part: only D.
        if (IsDigit(s[i]))
        {
            if (!ReadInteger(s, ref i, out var days) || i >= n || s[i] != 'D')
            {
                return false;
            }

            total += days * 86_400_000m;
            components++;
            i++;
        }

        if (i < n)
        {
            if (s[i] != 'T')
            {
                return false;
            }

            i++;
            int timeComponents = 0;
            int order = 0; // 1 = H, 2 = M, 3 = S; must strictly increase.
            while (i < n)
            {
                if (!ReadInteger(s, ref i, out var whole))
                {
                    return false;
                }

                decimal fraction = 0m;
                bool hasFraction = false;
                if (i < n && s[i] == '.')
                {
                    i++;
                    int start = i;
                    decimal scale = 1m;
                    while (i < n && IsDigit(s[i]))
                    {
                        scale /= 10m;
                        fraction += (s[i] - '0') * scale;
                        i++;
                    }

                    int digits = i - start;
                    if (digits == 0 || digits > 9)
                    {
                        return false;
                    }

                    hasFraction = true;
                }

                if (i >= n)
                {
                    return false;
                }

                int unitOrder;
                decimal unitMillis;
                switch (s[i])
                {
                    case 'H': unitOrder = 1; unitMillis = 3_600_000m; break;
                    case 'M': unitOrder = 2; unitMillis = 60_000m; break;
                    case 'S': unitOrder = 3; unitMillis = 1_000m; break;
                    default: return false;
                }

                if (unitOrder <= order || (hasFraction && unitOrder != 3))
                {
                    return false;
                }

                order = unitOrder;
                total += (whole + fraction) * unitMillis;
                timeComponents++;
                i++;
            }

            if (timeComponents == 0)
            {
                return false;
            }

            components += timeComponents;
        }

        return components > 0 && total <= MaxMillis;
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static bool ReadInteger(string s, ref int i, out decimal value)
    {
        value = 0m;
        int start = i;
        int significant = 0;
        while (i < s.Length && IsDigit(s[i]))
        {
            if (significant > 0 || s[i] != '0')
            {
                significant++;
            }

            if (significant > MaxSignificantDigits)
            {
                return false;
            }

            value = (value * 10m) + (s[i] - '0');
            i++;
        }

        return i > start;
    }
}
