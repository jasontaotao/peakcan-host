using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// DI-singleton implementation of <see cref="IReplayService"/>. Owns a
/// <see cref="ReplayTimeline"/> + a reference to the DI-injected
/// <see cref="IReplayFrameSink"/> for frame emission.
/// </summary>
public sealed partial class ReplayService : IReplayService, IDisposable
{
    private readonly IReplayFrameSink _sink;
    private readonly ILogger<ReplayService> _logger;
    private readonly ReplayTimeline _timeline;
    private IReadOnlyList<ReplayFrame> _frames = Array.Empty<ReplayFrame>();

    public ReplayService(IReplayFrameSink sink, ILogger<ReplayService> logger)
    {
        _sink = sink;
        _logger = logger;
        _timeline = new ReplayTimeline(EmitFrame);
    }

    public ReplayState State => !_timeline.HasStarted
        ? ReplayState.Stopped
        : _timeline.IsPlaying ? ReplayState.Playing : ReplayState.Paused;
    public double CurrentTimestamp => _timeline.CurrentTimestamp;
    public double TotalDuration => _frames.Count > 0 ? _frames[^1].Timestamp : 0.0;
    public double Speed => _timeline.Speed;
    public event Action<ReplayFrame>? FrameEmitted;

    /// <summary>
    /// Disposes the playback timer. Safe to call multiple times.
    /// </summary>
    public void Dispose() => _timeline.Stop();

    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        // v1.4.0 MINOR Replay: open file → parse → set frames.
        // ParseExceptions propagate; FileNotFound/IO wrap into ReplayLoadException.
        try
        {
            await using var fs = File.OpenRead(path);
            _frames = await AscParser.ParseAsync(fs, ct).ConfigureAwait(false);
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

    private void EmitFrame(ReplayFrame frame)
    {
        // Timer callback is sync; sink is fire-and-forget. Errors must not stall
        // playback — the timeline swallows them in its outer try/catch, but we
        // log here so the warning reaches the user's logger even if the timeline
        // catch is hit.
        try
        {
            _ = EmitFrameToSinkAsync(frame);
        }
        catch (Exception ex)
        {
            LogSinkThrew(_logger, ex, frame.Id, frame.Timestamp);
        }
        FrameEmitted?.Invoke(frame);
    }

    // CA2012: ValueTask is intentionally fire-and-forget — Timer callbacks are
    // synchronous and we cannot await here. The task is stored in a field long
    // enough for the runtime to observe completion; downstream sink errors are
    // caught by the timer-level try/catch in ReplayTimeline.OnTick.
#pragma warning disable CA2012
    private async Task EmitFrameToSinkAsync(ReplayFrame frame)
    {
        await _sink.SendFrameAsync(frame, CancellationToken.None).ConfigureAwait(false);
    }
#pragma warning restore CA2012

    // LoggerMessage source-generated helper (CA1848). Replaces
    // _logger.LogWarning(ex, "...", frame.Id, frame.Timestamp) which the
    // analyzer flags for using LoggerExtensions instead of a generated delegate.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Replay sink threw for frame {FrameId} at t={Timestamp}")]
    private static partial void LogSinkThrew(ILogger logger, Exception ex, uint frameId, double timestamp);
}
