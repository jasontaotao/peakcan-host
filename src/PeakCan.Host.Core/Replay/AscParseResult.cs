namespace PeakCan.Host.Core.Replay;

/// <summary>
/// v3.18.0 PATCH (Trace Viewer Enhancements): bundles the result of
/// <see cref="AscParser.ParseAsync"/> so the caller can read both the
/// frame list AND the ASC header metadata (wall-clock origin, base-hex
/// timestamp mode) without re-reading the stream.
/// <para>
/// <b>WallClockOrigin</b>: parsed from the <c>date Wed Jul 1 08:32:01.000
/// am 2026</c> header line. Null when the ASC has no <c>date</c> line
/// (≈5% of traces) or when the line is unparseable. The X-axis
/// formatter in TraceChartViewModel uses this to display wall-clock
/// labels; null falls back to elapsed-time display.
/// </para>
/// <para>
/// <b>TimestampsAreAbsolute</b>: parsed from <c>base hex  timestamps
/// absolute</c>. When true, the numeric column is seconds since the
/// <c>date</c> epoch; when false, the numeric column is relative to
/// the file's first frame. Currently informational only (the parser
/// stores the absolute-seconds value either way), but reserved for
/// future correctness checks (e.g. reject a relative file with a
/// date header, or vice versa).
/// </para>
/// </summary>
public sealed record AscParseResult(
    IReadOnlyList<ReplayFrame> Frames,
    DateTime? WallClockOrigin,
    bool TimestampsAreAbsolute);
