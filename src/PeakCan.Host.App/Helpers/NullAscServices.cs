using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;

namespace PeakCan.Host.App.Helpers;

/// <summary>
/// v3.6.4 PATCH: no-op <see cref="IAscContentHasher"/> used when no
/// hasher was injected. Returns the empty string for every path so
/// <c>BuildSnapshot</c> never blocks on disk I/O and every saved
/// bundle round-trips without a contentHash. Production DI wires
/// <see cref="Sha256AscContentHasher"/>; tests that care about hashing
/// inject their own fake.
/// </summary>
internal sealed class NullAscContentHasher : IAscContentHasher
{
    public static readonly NullAscContentHasher Instance = new();
    private NullAscContentHasher() { }
    public Task<string> ComputeAsync(string path, CancellationToken ct = default)
        => Task.FromResult("");
}

/// <summary>
/// v3.6.4 PATCH: no-op <see cref="IAscLocator"/> used when no locator
/// was injected. Always returns <c>null</c> so the ApplySnapshotAsync
/// hash fallback is a no-op and the existing path-only resolution
/// continues to surface the missing-path list. Production DI wires
/// <see cref="FileSystemAscLocator"/>.
/// </summary>
internal sealed class NullAscLocator : IAscLocator
{
    public static readonly NullAscLocator Instance = new();
    private NullAscLocator() { }
    public Task<string?> LocateAsync(string contentHash, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}

/// <summary>
/// v3.52.0 MINOR T9: no-op <see cref="IFrameSourceProvider"/> used when
/// no frame source was injected (legacy 4-arg test sites that don't
/// exercise the analysis pipeline). Returns an empty frame list so
/// <c>EvidenceExtractor.Extract</c> short-circuits cleanly if any test
/// unexpectedly triggers an analysis run. Production DI wires the
/// <c>TraceSessionRegistry</c> itself (dual-interface in T9).
/// </summary>
internal sealed class NullFrameSourceProvider : IFrameSourceProvider
{
    public static readonly NullFrameSourceProvider Instance = new();
    private NullFrameSourceProvider() { }
    public IReadOnlyList<ReplayFrame> GetFrames(string sourceId) => Array.Empty<ReplayFrame>();
}