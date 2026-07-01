namespace PeakCan.Host.Core;

/// <summary>
/// Strongly-typed CAN identifier with format-aware range validation.
/// <para>
/// A <c>CanId</c> is always (raw, format) — the same <c>Raw</c> value in two
/// different formats is conceptually a different identifier on the bus, and
/// callers cannot accidentally cross the wire.
/// </para>
/// </summary>
public readonly record struct CanId
{
    /// <summary>Raw identifier bits. 11-bit when <see cref="Format"/> is Standard, 29-bit when Extended.</summary>
    public uint Raw { get; }

    /// <summary>Frame format (Standard 11-bit / Extended 29-bit).</summary>
    public FrameFormat Format { get; }

    /// <summary>Frame payload type (Data / Remote / Error / Status). Mutable via <c>with</c>.</summary>
    public FrameType Type { get; init; }

    /// <param name="raw">Raw identifier bits (≤ 0x7FF for Standard, ≤ 0x1FFFFFFF for Extended).</param>
    /// <param name="format">Frame format (Standard / Extended).</param>
    /// <param name="type">Payload type. Defaults to <see cref="FrameType.Data"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">raw exceeds the maximum for the chosen format.</exception>
    public CanId(uint raw, FrameFormat format, FrameType type = FrameType.Data)
    {
        if (format == FrameFormat.Standard && raw > 0x7FFu)
            throw new ArgumentOutOfRangeException(nameof(raw), raw, "Standard ID exceeds 11 bits (max 0x7FF).");
        if (format == FrameFormat.Extended && raw > 0x1FFFFFFFu)
            throw new ArgumentOutOfRangeException(nameof(raw), raw, "Extended ID exceeds 29 bits (max 0x1FFFFFFF).");
        Raw = raw;
        Format = format;
        Type = type;
    }

    /// <summary>True iff this identifier uses the 29-bit extended format.</summary>
    public bool IsExtended => Format == FrameFormat.Extended;

    /// <summary>Render as 3-hex-digit (Standard) or 8-hex-digit (Extended) uppercase.</summary>
    public override string ToString()
        => IsExtended ? $"0x{Raw:X8}" : $"0x{Raw:X3}";
}