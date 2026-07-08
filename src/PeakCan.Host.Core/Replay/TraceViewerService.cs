using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Path;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Default <see cref="ITraceViewerService"/> impl. Loads ASC via
/// <see cref="AscParser"/>; plays frames via a private
/// <see cref="ReplayTimeline"/> that emits each frame to subscribers
/// via <see cref="FrameEmitted"/>. **No sink injection** — frames
/// never reach the CAN bus.
/// </summary>
public sealed class TraceViewerService : ITraceViewerService, IDisposable
{
    /// <summary>
    /// v3.9.1 PATCH Bug #2 size cap: refuse to open .asc files beyond
    /// 200 MB. A 200 MB .asc with ~30 bytes/frame ≈ 7M frames × ~24 bytes
    /// ≈ 170 MB heap just for the parsed frames, leaving headroom for
    /// OxyPlot series copies. Production 24h captures that exceed 200 MB
    /// should be pre-truncated with a tool. Mirrors the v3.8.8 PATCH F2
    /// pattern (<see cref="PeakCan.Host.App.Services.Trace.RecentSessionsService.MaxLoadFileBytes"/>).
    /// </summary>
    public const long MaxAscFileBytes = 200L * 1024 * 1024;

    private readonly ILogger<TraceViewerService> _logger;
    private readonly ReplayOptions _options;
    private readonly ReplayTimeline _timeline;
    private IReadOnlyList<ReplayFrame> _frames = Array.Empty<ReplayFrame>();

    /// <summary>
    /// v3.10.0 MINOR T4 (H5): 1-arg ctor retained for back-compat with
    /// existing test harnesses (TraceSessionRegistry, TraceViewerServiceTests)
    /// that construct via <c>new TraceViewerService(NullLogger&lt;...&gt;.Instance)</c>.
    /// Defaults <see cref="_options"/> to <see cref="ReplayOptions.Default"/>
    /// so the legacy code path remains cap-protected at 200 MB.
    /// </summary>
    public TraceViewerService(ILogger<TraceViewerService> logger)
        : this(logger, ReplayOptions.Default)
    {
    }

    /// <summary>
    /// v3.10.0 MINOR T4 (H5): 2-arg ctor threads the
    /// <see cref="ReplayOptions"/> cap down into <see cref="AscParser.ParseAsync"/>
    /// so the parser-layer cap can be dialed via appsettings.json without a
    /// recompile. The service-layer precheck
    /// (<see cref="MaxAscFileBytes"/> = 200 MB) is now duplicated by the
    /// parser-layer cap for defense-in-depth.
    /// </summary>
    public TraceViewerService(ILogger<TraceViewerService> logger, ReplayOptions options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _options = options;
        // v3.16.8 PATCH: SMOKE TEST log — if this line never appears in
        // %LOCALAPPDATA%/PeakCan.Host/logs/peak-{date}.log, the Serilog
        // pipeline itself is broken (or the user is looking at the wrong
        // file). Confirms that TraceViewerService was constructed AND
        // that the logger field is non-null AND that the Serilog sink
        // can write to the file.
        _logger.LogInformation("[SMOKE v3.16.8] TraceViewerService ctor ENTER; options.MaxFileSizeBytes={Max}",
            _options.MaxFileSizeBytes);
        _timeline = new ReplayTimeline(
            emit: EmitFrame,
            onPlaybackEnded: RaisePlaybackEnded,
            onSinkThrew: null,   // no sink — pass null
            // v3.16.7 PATCH: forward the service's logger so ReplayTimeline's
            // diagnostic logs (Play/OnTick entry, frame-emit count) actually
            // reach Serilog. Previously the timeline got NullLogger.Instance
            // and the logs were silently swallowed.
            logger: _logger);
    }

    public ReplayState State => !_timeline.HasStarted
        ? ReplayState.Stopped
        : _timeline.IsPlaying ? ReplayState.Playing : ReplayState.Paused;
    public double CurrentTimestamp => _timeline.CurrentTimestamp;
    public double TotalDuration => _frames.Count > 0 ? _frames[^1].Timestamp : 0.0;
    public double Speed => _timeline.Speed;
    public IReadOnlyList<ReplayFrame> LoadedFrames => _frames;
    public event Action<ReplayFrame>? FrameEmitted;

    public bool Loop
    {
        get => _timeline.Loop;
        set => _timeline.Loop = value;
    }
    public IReadOnlySet<uint>? CanIdFilter { get; set; }
    public double? StartTimestamp
    {
        get => _timeline.StartTimestamp;
        set => _timeline.StartTimestamp = value;
    }
    public double? EndTimestamp
    {
        get => _timeline.EndTimestamp;
        set => _timeline.EndTimestamp = value;
    }
    public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;

    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var normalized = PathNormalizer.Normalize(path);
            // v3.9.1 PATCH Bug #2 size-cap precheck (mirrors v3.8.8 PATCH F2).
            // Without this, a multi-GB .asc would happily open, consume
            // hundreds of MB of heap for frames, and freeze the WPF
            // dispatcher for the entire AscParser.ParseAsync walk. Use
            // FileInfo.Length — cheap stat call, no actual file read.
            var info = new FileInfo(normalized);
            if (info.Length > MaxAscFileBytes)
                throw new ReplayLoadException(
                    $"ASC file exceeds size cap ({info.Length:N0} > {MaxAscFileBytes:N0} bytes); use a tool to truncate: {path}");
            // useAsync: true + 4096 buffer for true async I/O on .NET 10
            // (File.OpenRead uses synchronous FileStream by default).
            await using var fs = new FileStream(
                normalized,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            // v3.10.0 MINOR T4 (H5): thread the ReplayOptions cap down to
            // the parser layer for defense-in-depth. The service-layer
            // precheck above (FileInfo.Length > MaxAscFileBytes) already
            // gates the .asc file on disk; the parser-layer cap protects
            // direct AscParser callers (e.g. tests, future replay
            // pipelines) and gives AscParser itself an OOM guardrail.
            // Pass `null` for logger explicitly to disambiguate from the
            // 2-arg (Stream, CancellationToken) overload.
            _frames = await AscParser.ParseAsync(fs, _options, null, ct).ConfigureAwait(false);
        }
        catch (ReplayException) { throw; }
        catch (FileNotFoundException ex)
        {
            throw new ReplayLoadException($"ASC file not found: {path}", ex);
        }
        catch (Exception ex)
        {
            throw new ReplayLoadException($"Failed to read ASC file: {path}", ex);
        }
        _timeline.SetFrames(_frames);
    }

    public void Play() => _timeline.Play();
    public void Pause() => _timeline.Pause();
    public void Resume() => _timeline.Play();
    public void Seek(double timestamp) => _timeline.Seek(timestamp);
    public void SetSpeed(double multiplier) => _timeline.SetSpeed(multiplier);
    public void Stop() => _timeline.Stop();
    public void Dispose() => _timeline.Stop();

    private void EmitFrame(ReplayFrame frame)
    {
        var filter = CanIdFilter;
        if (filter is not null && !filter.Contains(frame.Id))
        {
            return;
        }
        FrameEmitted?.Invoke(frame);
    }

    private void RaisePlaybackEnded(PlaybackEndedEventArgs args)
        => PlaybackEnded?.Invoke(this, args);
}