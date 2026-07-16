using System.Globalization;

namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// v3.50.6 PATCH: numeric format helpers that derive the minimum decimal digits from a DBC <c>factor</c>.
/// </summary>
public static class SignalFormatter
{
    public static int ResolveDecimalDigits(double factor)
    {
        if (double.IsNaN(factor) || double.IsInfinity(factor) || factor == 0.0) return 0;
        var absF = Math.Abs(factor);
        var approx = -Math.Log10(absF);
        var rounded = Math.Round(approx);
        if (Math.Abs(approx - rounded) < 1e-9) return Math.Max(0, (int)rounded);
        if (absF < 1.0)
        {
            var frac = SimplifyFraction(absF);
            if (frac is { Denom: > 1 }) return MinTerminatingDigits(frac.Value.Denom);
        }
        return Math.Max(0, (int)Math.Ceiling(approx));
    }

    private static int MinTerminatingDigits(long denom)
    {
        long pow = 10;
        for (int k = 1; k <= 16; k++)
        {
            if (pow % denom == 0) return k;
            pow *= 10;
        }
        return Math.Max(0, (int)Math.Ceiling(Math.Log10(denom)));
    }

    public static string FormatValue(double factor, double value)
    {
        var digits = ResolveDecimalDigits(factor);
        return value.ToString("F" + digits, CultureInfo.InvariantCulture);
    }

    private static (long Numer, long Denom)? SimplifyFraction(double value)
    {
        for (long denom = 1; denom <= 10000; denom++)
        {
            var numer = (long)Math.Round(value * denom);
            if (numer <= 0) continue;
            if (Math.Abs((double)numer / denom - value) < 1e-9) return (numer, denom);
        }
        return null;
    }
}
