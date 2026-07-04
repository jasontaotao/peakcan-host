using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.2.0 MINOR: owns N <see cref="ITraceViewerService"/> instances for
/// the multi-trace overlay. Replaces the v3.0/3.1.x singleton
/// <see cref="ITraceViewerService"/> registration as the integration
/// point for the Trace Viewer window. The single-trace workflow
/// (1 source) is a degenerate case of this registry.
/// <para>
/// Defensive deep-copy contract: <see cref="GetFrames"/> returns a fresh
/// <c>ReplayFrame[]</c> snapshot per call so concurrent consumers cannot
/// observe each other's mutations through the registry's view. The
/// underlying <see cref="ITraceViewerService.LoadedFrames"/> exposes
/// internal storage directly (no defensive copy); the registry is the
/// boundary where the copy is enforced.
/// </para>
/// </summary>
public interface ITraceSessionRegistry
{
    IReadOnlyList<TraceSource> Sources { get; }

    /// <summary>Fires when <see cref="Sources"/> changes (Load or Unload).</summary>
    event Action? SourcesChanged;

    /// <summary>
    /// Loads an ASC file as a new trace in the session. Returns the
    /// metadata for the new source. Throws <see cref="InvalidOperationException"/>
    /// past palette capacity (10 sources).
    /// </summary>
    Task<TraceSource> LoadAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Removes the source from the session and disposes its underlying
    /// <see cref="ITraceViewerService"/>. No-op if sourceId is unknown.
    /// </summary>
    Task UnloadAsync(string sourceId);

    /// <summary>
    /// Returns a fresh defensive copy of the source's loaded frames.
    /// Always a new <c>ReplayFrame[]</c> — never the internal list.
    /// </summary>
    IReadOnlyList<ReplayFrame> GetFrames(string sourceId);

    /// <summary>
    /// v3.2.0 MINOR: returns the underlying <see cref="ITraceViewerService"/>
    /// for a source so the ViewModel can drive playback (Play / Pause /
    /// Stop / Seek) on the master source. Returns <c>null</c> if the
    /// sourceId is unknown. Callers should treat the result as
    /// read-only with respect to lifecycle (do not Dispose).
    /// </summary>
    ITraceViewerService? GetService(string sourceId);
}