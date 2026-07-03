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
    private readonly ILogger<TraceViewerService> _logger;
    private readonly ReplayTimeline _timeline;
    private IReadOnlyList<ReplayFrame> _frames = Array.Empty<ReplayFrame>();

    public TraceViewerService(ILogger<TraceViewerService> logger)
    {
        _logger = logger;
        _timeline = new ReplayTimeline(
            emit: EmitFrame,
            onPlaybackEnded: RaisePlaybackEnded,
            onSinkThrew: null);   // no sink — pass null
    }

    public ReplayState State => !_timeline.HasStarted
        ? ReplayState.Stopped
        : _timeline.IsPlaying ? ReplayState.Playing : ReplayState.Paused;
    public double CurrentTimestamp => _timeline.CurrentTimestamp;
    public double TotalDuration => _frames.Count > 0 ? _frames[^1].Timestamp : 0.0;
    public double Speed => _timeline.Speed;
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
            await using var fs = File.OpenRead(PathNormalizer.Normalize(path));
            _frames = await AscParser.ParseAsync(fs, ct).ConfigureAwait(false);
        }
        catch (ReplayFormatException) when (_frames.Count == 0)
        {
            // Empty file (0 parseable frames) — treat as a successful no-op load.
            // TotalDuration stays 0.0, State stays Stopped. Other format failures
            // (corrupted file with >50% malformed lines) still throw.
            _frames = Array.Empty<ReplayFrame>();
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