namespace PeakCan.Host.Core;

/// <summary>
/// Microsecond-resolution monotonic timestamp attached to a received frame.
/// <para>
/// Built from a PCAN-Basic <c>TPCANTimestamp</c> where the high word is whole
/// milliseconds and the low word is microseconds within that millisecond.
/// </para>
/// </summary>
public readonly record struct Timestamp(ulong TotalMicroseconds)
{
    /// <summary>Compose from PCAN-Basic millis/micros parts.</summary>
    public static Timestamp FromMillis(ulong millis, ushort micros)
        => new(millis * 1000UL + micros);

    /// <summary>Format as <c>HH:mm:ss.ffffff</c> for UI display.</summary>
    public override string ToString()
    {
        var secs = TotalMicroseconds / 1_000_000UL;
        var frac = TotalMicroseconds % 1_000_000UL;
        var hours = secs / 3600UL;
        var mins = (secs % 3600UL) / 60UL;
        var s = secs % 60UL;
        return $"{hours:D2}:{mins:D2}:{s:D2}.{frac:D6}";
    }
}