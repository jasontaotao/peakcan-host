using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Services;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.11.0 MINOR T2 (H7): shared BuildSnapshot logic for Trace +
/// Replay VMs. Both VMs construct a <see cref="TraceSessionBundleDto"/>
/// with the same scalar envelope fields and the same content-hash
/// computation pattern. Extracting the shape here closes the
/// parallel-evolution risk and removes ~80 LoC of duplicate logic.
/// <para>
/// <b>Scope:</b> the builder handles the scalar envelope + the
/// content-hash computation for the (single) loaded .asc. When the
/// scaffold's <see cref="Scaffold.LoadedFilePath"/> is set and points
/// at an existing file, the returned DTO has a single-entry
/// <see cref="TraceSessionBundleDto.Sources"/> list carrying the
/// computed hash. VMs with VM-specific sources (Trace's N sources +
/// viewports, Replay's playback envelope) overwrite
/// <see cref="TraceSessionBundleDto.Sources"/>,
/// <see cref="TraceSessionBundleDto.Playback"/>, and
/// <see cref="TraceSessionBundleDto.Viewports"/> after this method
/// returns — but the Replay VM (single source) can use the builder's
/// pre-populated single source as a starting point.
/// </para>
/// <para>
/// <b>Sync-vs-async:</b> the builder exposes an async entry point
/// (<see cref="BuildAsync"/>). The VMs keep a thin sync shim
/// <c>BuildSnapshot()</c> that calls
/// <c>BuildAsync().GetAwaiter().GetResult()</c> for back-compat with
/// existing callers that don't have a CT handy (auto-saver is
/// refactored in T3).
/// </para>
/// </summary>
public sealed partial class TraceSessionSnapshotBuilder
{
    private readonly IAscContentHasher _hasher;
    private readonly ILogger<TraceSessionSnapshotBuilder> _logger;

    /// <summary>Production ctor: pass the real hasher + optional logger.</summary>
    public TraceSessionSnapshotBuilder(
        IAscContentHasher hasher,
        ILogger<TraceSessionSnapshotBuilder>? logger = null)
    {
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _logger = logger ?? NullLogger<TraceSessionSnapshotBuilder>.Instance;
    }

    /// <summary>
    /// Caller-supplied scaffold for the snapshot envelope fields.
    /// Mirrors the VM's bindable state at Build time so the builder
    /// never has to know about VM properties. Declared
    /// <c>public</c> so the calling VMs can construct it from
    /// their ctor / property state without an extra mapping layer.
    /// </summary>
    public sealed record Scaffold(
        string? LoadedFilePath,
        double CurrentTimestamp,
        double Speed,
        bool Loop,
        double StartTimestamp,
        double EndTimestamp,
        string CanIdFilterText,
        string DbcPath);

    /// <summary>
    /// Compute the content hash (when applicable) and assemble the
    /// scalar envelope. When <see cref="Scaffold.LoadedFilePath"/> is
    /// non-empty and points at an existing file, the returned DTO
    /// carries a single <see cref="BundleSourceDto"/> in
    /// <see cref="TraceSessionBundleDto.Sources"/> with the computed
    /// hash pre-populated. Otherwise
    /// <see cref="TraceSessionBundleDto.Sources"/> is an empty list.
    /// <see cref="TraceSessionBundleDto.Playback"/> and
    /// <see cref="TraceSessionBundleDto.Viewports"/> are always left
    /// for the calling VM to populate.
    /// <para>
    /// Cancellation propagates from <paramref name="ct"/> through the
    /// hasher. IO-level failures (<see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/>,
    /// <see cref="System.Security.SecurityException"/>) are caught and
    /// the hash falls back to empty so the bundle still saves
    /// (path-only resolution covers reload).
    /// </para>
    /// </summary>
    public async Task<TraceSessionBundleDto> BuildAsync(
        Scaffold scaffold,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scaffold);

        var dto = new TraceSessionBundleDto
        {
            Version = 1,
            Schema = TraceSessionLibrary.CurrentSchema,
            SavedAt = DateTimeOffset.UtcNow,
            AppVersion = GetAppVersion(),
            DbcPath = scaffold.DbcPath ?? "",
            GlobalCanIdFilter = scaffold.CanIdFilterText ?? "",
        };

        if (!string.IsNullOrEmpty(scaffold.LoadedFilePath) && File.Exists(scaffold.LoadedFilePath))
        {
            var hash = "";
            try
            {
                hash = await _hasher.ComputeAsync(scaffold.LoadedFilePath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Hashing failed (locked file / ACL / trust). Skip — the
                // bundle still saves with contentHash="" and the
                // path-only resolution covers it on reload. Mirrors the
                // pre-extraction behavior in ReplayViewModel.BuildSnapshot
                // and TraceViewerViewModel.BuildSnapshot (v3.6.4 PATCH).
                LogHashFailed(_logger, ex, scaffold.LoadedFilePath);
                hash = "";
            }
            // Single pre-populated source. Replay's BuildSnapshotAsync
            // overwrites this with its own BundleSourceDto (carrying
            // the per-source display name + GUID id) but uses this as
            // the hash carrier on the first element. Trace's
            // BuildSnapshotAsync overwrites the entire Sources list
            // because it iterates _registry.Sources (N entries).
            dto.Sources = new List<BundleSourceDto>
            {
                new()
                {
                    Path = scaffold.LoadedFilePath,
                    ContentHash = hash,
                },
            };
        }

        return dto;
    }

    /// <summary>
    /// 版本口径统一走 <see cref="AppVersion.Semantic"/>（x.y.z，已去掉
    /// <c>+&lt;commit sha&gt;</c>），与窗口标题同源——两处各写一份截断规则，
    /// 迟早会出现「标题显示 3.66.1、快照记成别的形状」。
    /// </summary>
    private static string GetAppVersion() => AppVersion.Semantic;

    [LoggerMessage(Level = LogLevel.Warning, Message = "BuildSnapshot: hashing failed for {Path}; bundle saved without contentHash")]
    private static partial void LogHashFailed(ILogger logger, Exception ex, string? path);
}