using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Services.Uds;

/// <summary>
/// Loads an ASC/BLF trace for offline UDS analysis. The loader owns file IO and
/// the size cap; the Core analyzer only ever sees an in-memory frame list.
/// </summary>
public interface IUdsOfflineTraceLoader
{
    Task<UdsOfflineTraceLoadResult> LoadAsync(string path, CancellationToken ct = default);
}

/// <summary><see cref="Format"/> is the source format name ("ASC" / "BLF") shown in the report.</summary>
public sealed record UdsOfflineTraceLoadResult(IReadOnlyList<ReplayFrame> Frames, string Format);

/// <summary>
/// Explicit, user-facing load failure. The message is shown verbatim in the
/// status bar, so it must state what went wrong and the actual limit in force.
/// </summary>
public sealed class UdsOfflineLoadException(string message) : Exception(message);
