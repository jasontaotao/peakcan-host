using FluentAssertions;
using NSubstitute;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// v1.4.0 MINOR Replay: <see cref="ReplayViewModel"/> wraps
/// <see cref="IReplayService"/> for the Replay tab. Exposes Open, Play,
/// Pause, Stop, Seek and SetSpeed commands, plus bindable playback state
/// (IsPlaying / IsPaused / Speed / CurrentTimestamp / TotalDuration /
/// ScrubberMaxValue / LoadedFilePath / ErrorMessage / IsLoaded).
/// <para>
/// All four tests run against NSubstitute mocks of
/// <see cref="IReplayService"/> and <see cref="IFileDialogService"/> — no
/// WPF Application instance is created, so there is no STA-WPF xunit race
/// per memory v1.2.11 lessons.
/// </para>
/// </summary>
public class ReplayViewModelTests : IDisposable
{
    private readonly IReplayService _service = Substitute.For<IReplayService>();
    private readonly IFileDialogService _fileDialog = Substitute.For<IFileDialogService>();
    private readonly ReplayViewModel _sut;

    public ReplayViewModelTests()
    {
        _service.TotalDuration.Returns(10.0);
        _sut = new ReplayViewModel(_service, _fileDialog);
    }

    public void Dispose()
    {
        _sut.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// v1.4.0 MINOR: <c>OpenCommand</c> resolves a path via the dialog
    /// service, calls <see cref="IReplayService.LoadAsync"/>, and
    /// populates <c>TotalDuration</c>, <c>ScrubberMaxValue</c>, and
    /// <c>LoadedFilePath</c> from the parsed file. <c>CurrentTimestamp</c>
    /// resets to 0 so the slider thumb starts at the beginning.
    /// </summary>
    [Fact]
    public async Task OpenAsync_PopulatesTotalDurationAndScrubberMax()
    {
        _fileDialog.ShowOpenDialog(Arg.Any<string>()).Returns("/tmp/test.asc");
        _service.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _service.TotalDuration.Returns(5.5);

        await _sut.OpenCommand.ExecuteAsync(null);

        _sut.TotalDuration.Should().Be(5.5);
        _sut.ScrubberMaxValue.Should().Be(5.5);
        _sut.LoadedFilePath.Should().Be("/tmp/test.asc");
        _sut.IsLoaded.Should().BeTrue();
        _sut.CurrentTimestamp.Should().Be(0.0);
        await _service.Received(1).LoadAsync("/tmp/test.asc", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// v1.4.0 MINOR: <c>OpenCommand</c> with the dialog returning
    /// <c>null</c> (user cancel) is a no-op: no load, file path stays
    /// empty, total duration unchanged.
    /// </summary>
    [Fact]
    public async Task OpenAsync_Dialog_Cancelled_Is_NoOp()
    {
        _fileDialog.ShowOpenDialog(Arg.Any<string>()).Returns((string?)null);

        await _sut.OpenCommand.ExecuteAsync(null);

        await _service.DidNotReceive().LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _sut.LoadedFilePath.Should().BeNull();
        _sut.IsLoaded.Should().BeFalse();
        // TotalDuration stays at 0 (default) — the VM only reads it from
        // the service inside OpenAsync AFTER a successful load.
    }

    /// <summary>
    /// v1.4.0 MINOR: <c>OpenCommand</c> wraps
    /// <see cref="ReplayException"/> into <c>ErrorMessage</c> and clears
    /// <c>IsLoaded</c> so the UI greys out the transport bar.
    /// </summary>
    [Fact]
    public async Task OpenAsync_ReplayException_PopulatesErrorMessage()
    {
        _fileDialog.ShowOpenDialog(Arg.Any<string>()).Returns("/tmp/malformed.asc");
        _service.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new ReplayFormatException("bad asc"));

        await _sut.OpenCommand.ExecuteAsync(null);

        _sut.ErrorMessage.Should().Be("bad asc");
        _sut.IsLoaded.Should().BeFalse();
        _sut.LoadedFilePath.Should().BeNull();
    }

    /// <summary>
    /// v1.4.0 MINOR: <c>PlayCommand</c> drives <see cref="IReplayService.Play"/>
    /// and mirrors <c>State == Playing</c> into <c>IsPlaying=true</c>,
    /// <c>IsPaused=false</c>. The service's <c>State</c> is the source of
    /// truth — the VM does not locally invent state.
    /// </summary>
    [Fact]
    public void Play_TogglesIsPlaying_AndCallsService()
    {
        _service.State.Returns(ReplayState.Playing);

        _sut.PlayCommand.Execute(null);

        _service.Received(1).Play();
        _sut.IsPlaying.Should().BeTrue();
        _sut.IsPaused.Should().BeFalse();
    }

    /// <summary>
    /// v1.4.0 MINOR: <c>PauseCommand</c> drives
    /// <see cref="IReplayService.Pause"/> and reflects the result into
    /// <c>IsPlaying=false</c>, <c>IsPaused=true</c>.
    /// </summary>
    [Fact]
    public void Pause_TogglesIsPaused_AndCallsService()
    {
        _sut.PauseCommand.Execute(null);

        _service.Received(1).Pause();
        _sut.IsPlaying.Should().BeFalse();
        _sut.IsPaused.Should().BeTrue();
    }

    /// <summary>
    /// v1.4.0 MINOR: <c>StopCommand</c> drives
    /// <see cref="IReplayService.Stop"/> and resets <c>IsPlaying</c>,
    /// <c>IsPaused</c>, and <c>CurrentTimestamp</c> so the slider thumb
    /// snaps back to 0.
    /// </summary>
    [Fact]
    public void Stop_ResetsState_AndCurrentTimestamp()
    {
        _sut.PlayCommand.Execute(null);
        _sut.CurrentTimestamp = 3.5;

        _sut.StopCommand.Execute(null);

        _service.Received(1).Stop();
        _sut.IsPlaying.Should().BeFalse();
        _sut.IsPaused.Should().BeFalse();
        _sut.CurrentTimestamp.Should().Be(0.0);
    }

    /// <summary>
    /// v1.4.0 MINOR: <c>SeekToCommand</c> drives
    /// <see cref="IReplayService.Seek"/> and updates
    /// <c>CurrentTimestamp</c> immediately to reflect the slider's new
    /// position (the slider binding is TwoWay so this keeps the two in
    /// sync without waiting for the next timer tick).
    /// </summary>
    [Fact]
    public void SeekTo_UpdatesCurrentTimestamp_AndCallsService()
    {
        _sut.SeekToCommand.Execute(2.5);

        _service.Received(1).Seek(2.5);
        _sut.CurrentTimestamp.Should().Be(2.5);
    }

    /// <summary>
    /// v1.4.0 MINOR: <c>SetSpeedCommand</c> drives
    /// <see cref="IReplayService.SetSpeed"/> and updates the bindable
    /// <c>Speed</c> property.
    /// </summary>
    [Fact]
    public void SetSpeed_UpdatesSpeed_AndCallsService()
    {
        _sut.SetSpeedCommand.Execute(2.0);

        _service.Received(1).SetSpeed(2.0);
        _sut.Speed.Should().Be(2.0);
    }

    /// <summary>
    /// v1.4.0 MINOR: <c>SetSpeedCommand</c> rejects non-positive
    /// multipliers (per <see cref="IReplayService.SetSpeed"/> contract —
    /// the timeline requires multiplier &gt; 0). The command is a
    /// no-op and <c>Speed</c> is unchanged.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void SetSpeed_NonPositive_Is_NoOp(double multiplier)
    {
        _sut.Speed = 1.0;

        _sut.SetSpeedCommand.Execute(multiplier);

        _service.DidNotReceive().SetSpeed(Arg.Any<double>());
        _sut.Speed.Should().Be(1.0);
    }
}
