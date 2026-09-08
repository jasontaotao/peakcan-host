using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Mobile.Core.Tests.Fakes;

/// <summary>Records calls + lets tests drive emission/events. Implements the contract loosely enough for VM tests.</summary>
public sealed class FakeStreamingTracePlayer : IStreamingTracePlayer
{
    public ReplayState State { get; set; } = ReplayState.Stopped;
    public double CurrentTimestamp { get; set; }
    public double Speed { get; set; } = 1.0;
    public long FramesEmitted { get; set; }
    public long SkippedLines { get; set; }
    public event Action<ReplayFrame>? FrameEmitted;
    public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;
    public event Action<double>? SeekProgress;
    public int PlayCount { get; set; }
    public int PauseCount { get; set; }
    public int ResumeCount { get; set; }
    public int StopCount { get; set; }
    public List<double> Seeks { get; } = new();

    public Task PlayAsync(CancellationToken ct = default)
    {
        PlayCount++;
        State = ReplayState.Playing;
        return Task.CompletedTask;
    }

    public void Pause()
    {
        PauseCount++;
        State = ReplayState.Paused;
    }

    public void Resume()
    {
        ResumeCount++;
        State = ReplayState.Playing;
    }

    public void SetSpeed(double multiplier) => Speed = multiplier;
    public Task SeekAsync(double timestamp, CancellationToken ct = default)
    {
        Seeks.Add(timestamp);
        return Task.CompletedTask;
    }

    public void Stop()
    {
        StopCount++;
        State = ReplayState.Stopped;
    }

    public void Emit(ReplayFrame f)
    {
        CurrentTimestamp = f.Timestamp;
        FramesEmitted++;
        FrameEmitted?.Invoke(f);
    }

    public void EmitSeekProgress(double progress) => SeekProgress?.Invoke(progress);
    public void EmitEof() => PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs());
    public void Dispose() { }
}
