namespace PeakCan.Host.Core.Replay;

/// <summary>
/// v3.10.0 MINOR T4 (H5): runtime configuration for ASC replay.
/// Bound from <c>Replay:</c> section of appsettings.json by the
/// App-layer DI factory (mirrors the <c>DbcOptions</c> / <c>PathOptions</c>
/// precedent).
/// </summary>
/// <param name="MaxFileSizeBytes">
/// Maximum raw byte length of the ASC stream the parser is willing to
/// walk. Defense-in-depth at the parser layer so direct callers (e.g.
/// tests, replay pipelines) cannot accidentally load a multi-GB stream
/// and freeze the WPF dispatcher. The service layer
/// (<see cref="TraceViewerService"/>) already prechecks via
/// <see cref="TraceViewerService.MaxAscFileBytes"/>; this knob lets
/// operators dial the cap via appsettings.json without a recompile.
/// Default = <see cref="DefaultMaxFileSizeBytes"/> (200 MB).
/// </param>
public sealed record ReplayOptions(long MaxFileSizeBytes)
{
    /// <summary>
    /// v3.10.0 MINOR T4 (H5): 200 MB default — mirrors
    /// <see cref="TraceViewerService.MaxAscFileBytes"/>. A 200 MB
    /// .asc with ~30 bytes/frame ≈ 7M frames × ~24 bytes ≈ 170 MB
    /// heap just for the parsed frames, leaving headroom for
    /// OxyPlot series copies.
    /// </summary>
    public const long DefaultMaxFileSizeBytes = 200L * 1024 * 1024;

    /// <summary>
    /// v3.10.0 MINOR T4 (H5): parameterless ctor returns the 200 MB default.
    /// Mirrors <c>DbcOptions.Unlimited</c> precedent for a singleton
    /// default that legacy overloads can reuse.
    /// </summary>
    public ReplayOptions() : this(DefaultMaxFileSizeBytes) { }

    /// <summary>
    /// v3.10.0 MINOR T4 (H5): singleton default instance used by
    /// the legacy <c>ParseAsync(Stream, ILogger?, CancellationToken)</c>
    /// overload + the 1-arg <see cref="TraceViewerService"/> ctor so
    /// existing test harnesses keep compiling.
    /// </summary>
    public static ReplayOptions Default { get; } = new();
}