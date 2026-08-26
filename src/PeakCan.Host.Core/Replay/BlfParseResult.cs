namespace PeakCan.HIL.Core.Replay;

/// <summary>
/// v3.17.0 PATCH (BLF playback fix): bundles the result of
/// <see cref="BlfParser.ParseAsyncWithOrigin"/> so the caller receives both
/// the relativized frame list AND the wall-clock origin (absolute first-frame
/// time). Sister of <see cref="AscParseResult"/>.
/// <para>
/// <b>Frames</b>: BLF object_time_stamp values (1-nanosecond ticks since the
/// 1970 Vector epoch) divided by <see cref="BlfFormat.TimestampScale"/> to seconds,
/// then <b>relativized</b> by subtracting the minimum frame timestamp. The
/// first-emitted frame's relative timestamp is 0.0. This matches the
/// <see cref="ReplayFrame"/> contract ("seconds from recording start") and
/// the relative-timestamp assumption baked into
/// <see cref="ReplayTimeline"/>'s <c>PlayedTimestamp</c> comparison
/// (<c>frame.Timestamp &lt;= now</c>, where <c>now</c> grows from 0).
/// </para>
/// <para>
/// <b>WallClockOrigin</b>: the pre-relativization minimum absolute
/// timestamp expressed as a UTC <see cref="DateTime"/> (<c>VectorEpoch +
/// minAbsoluteSeconds</c>). Null only when the frame list is empty (which
/// <see cref="BlfParser.ParseAsync"/> rejects before constructing this
/// record). Reserved for future X-axis wall-clock label display (sister of
/// <c>AscParseResult.WallClockOrigin</c>); no live consumer wires it yet.
/// </para>
/// </summary>
public sealed record BlfParseResult(
    IReadOnlyList<ReplayFrame> Frames,
    DateTime? WallClockOrigin);
