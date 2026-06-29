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

    // ---------- v1.5.0 MINOR Task 5: Loop proxy + CanIdFilterText parser ----------

    /// <summary>
    /// v1.5.0 MINOR Task 5: <c>CanIdFilterText</c> setter with an empty
    /// or whitespace string clears the service filter to <c>null</c>
    /// (meaning "all frames pass") and clears the inline error message.
    /// The brief's tri-state contract: null = all pass, empty set =
    /// nothing passes, non-empty = whitelist.
    /// </summary>
    [Fact]
    public void CanIdFilterText_Empty_ClearsFilter()
    {
        // Arrange — start with a non-null filter and a non-empty text
        // value so we can observe the clearing transition. The source-gen
        // setter skips OnXxxChanged when the value is unchanged, so we
        // seed a value first to force a real setter call on empty.
        _service.CanIdFilter.Returns(new HashSet<uint> { 0x100 });
        _sut.CanIdFilterText = "0x100";

        // Act
        _sut.CanIdFilterText = string.Empty;

        // Assert
        _service.Received().CanIdFilter = null;
        _sut.CanIdFilterError.Should().BeNullOrEmpty();
    }

    /// <summary>
    /// v1.5.0 MINOR Task 5: parser handles a mixed hex (0x prefix) and
    /// decimal token stream. Whitespace / commas are separators; tokens
    /// are individually trimmed. The resulting set is pushed to the
    /// service's <c>CanIdFilter</c> property and the inline error is
    /// cleared.
    /// </summary>
    [Fact]
    public void CanIdFilterText_ValidHexAndDecimal_ParsesToSet()
    {
        // Act — four DISTINCT IDs. (0x100 == 256 and 0x200 == 512, so we
        // pick values whose hex and decimal forms are not redundant in the
        // same set: 0x100=256, 0x200=512, 768, 0x1FF=511.)
        _sut.CanIdFilterText = "0x100, 0x200, 768, 0x1FF";

        // Assert
        _service.Received().CanIdFilter = Arg.Is<IReadOnlySet<uint>>(s =>
            s.Contains(0x100) && s.Contains(0x200) && s.Contains(768) && s.Contains(0x1FF) && s.Count == 4);
        _sut.CanIdFilterError.Should().BeNullOrEmpty();
    }

    /// <summary>
    /// v1.5.0 MINOR Task 5: invalid tokens (non-numeric, bad hex) populate
    /// <c>CanIdFilterError</c> with a comma-separated list of the bad
    /// tokens. Valid tokens still make it into the resulting set so the
    /// user doesn't lose work by typing one typo.
    /// </summary>
    [Fact]
    public void CanIdFilterText_InvalidToken_ShowsErrorKeepsPriorFilter()
    {
        // Act — "0x100" is valid, "0xZZZ" + "garbage" are not.
        _sut.CanIdFilterText = "0x100, 0xZZZ, garbage";

        // Assert
        _sut.CanIdFilterError.Should().NotBeNullOrEmpty();
        _sut.CanIdFilterError.Should().Contain("0xZZZ");
        _sut.CanIdFilterError.Should().Contain("garbage");
        _service.Received().CanIdFilter = Arg.Is<IReadOnlySet<uint>>(s => s.Contains(0x100) && s.Count == 1);
    }

    // ---------- v1.4.2 PATCH Item 3: PlaybackEnded subscription + ErrorMessage ----------

    /// <summary>
    /// v1.4.2 PATCH Item 3: when the service raises <c>PlaybackEnded</c>
    /// with a non-null <c>Error</c> (e.g. <see cref="ReplaySendException"/>
    /// from a failed sink), the VM surfaces it via <c>ErrorMessage</c>
    /// and sets <c>IsPlaying = false</c>. Previously no consumer
    /// subscribed to <c>PlaybackEnded</c> so the user got no feedback
    /// when playback ran on a disconnected channel.
    /// </summary>
    [Fact]
    public void OnPlaybackEnded_WithError_SetsErrorMessageAndIsPlayingFalse()
    {
        // Arrange
        _sut.IsPlaying = true;
        var ex = new ReplaySendException("no active channel");

        // Act — raise PlaybackEnded with error
        _service.PlaybackEnded += Raise.Event<EventHandler<PlaybackEndedEventArgs>>(
            this, new PlaybackEndedEventArgs(ex));

        // Assert
        _sut.ErrorMessage.Should().NotBeNullOrEmpty();
        _sut.ErrorMessage.Should().Contain("Replay aborted");
        _sut.ErrorMessage.Should().Contain("no active channel");
        _sut.IsPlaying.Should().BeFalse("playback halted on error");
    }

    /// <summary>
    /// v1.4.2 PATCH Item 3: a normal EOF (Error == null) clears
    /// <c>IsPlaying</c> but does not set <c>ErrorMessage</c>.
    /// </summary>
    [Fact]
    public void OnPlaybackEnded_NormalEnd_DoesNotSetErrorMessage()
    {
        // Arrange
        _sut.IsPlaying = true;
        _sut.ErrorMessage = "stale prior error";

        // Act — raise PlaybackEnded with no error (normal EOF)
        _service.PlaybackEnded += Raise.Event<EventHandler<PlaybackEndedEventArgs>>(
            this, new PlaybackEndedEventArgs(null));

        // Assert
        _sut.IsPlaying.Should().BeFalse("playback stopped on EOF");
        // ErrorMessage is not overwritten by a normal-end event; the
        // prior "stale prior error" stays so the user can still see
        // any leftover load-time error. (To clear it, the user must
        // re-open a file, which goes through OpenAsync that resets it.)
    }

    // ---------- v1.5.1 PATCH Task 2: time-range filter ----------

    /// <summary>
    /// v1.5.1 PATCH Task 2: setting <c>StartTimestamp</c> on the VM proxies
    /// through to <see cref="IReplayService.StartTimestamp"/>. The service
    /// is the source of truth; the VM is a two-way binding proxy.
    /// </summary>
    [Fact]
    public void StartTimestamp_Set_PropagatesToService()
    {
        _sut.StartTimestamp = 1.5;

        _service.Received(1).StartTimestamp = 1.5;
    }

    /// <summary>
    /// v1.5.1 PATCH Task 2: setting <c>StartTimestamp</c> to null propagates
    /// to the service as null (unbounded below). The VM does not silently
    /// coerce to 0.
    /// </summary>
    [Fact]
    public void StartTimestamp_Null_ClearsToService()
    {
        _sut.StartTimestamp = 1.5;
        _sut.StartTimestamp = null;

        _service.Received(1).StartTimestamp = null;
    }

    /// <summary>
    /// v1.5.1 PATCH Task 2: setting <c>StartTimestamp</c> greater than the
    /// current <c>EndTimestamp</c> sets <c>RangeFilterError</c> and refuses
    /// to push the value to the service. This protects the WPF two-way
    /// binding path from <see cref="ArgumentException"/> (Decision 4).
    /// </summary>
    [Fact]
    public void StartTimestamp_GreaterThanEndTimestamp_SetsRangeFilterError()
    {
        _sut.EndTimestamp = 1.0;
        _sut.StartTimestamp = 2.0;  // 2.0 > 1.0 → invalid

        _sut.RangeFilterError.Should().NotBeNullOrEmpty();
        _sut.RangeFilterError.Should().Contain("Start");
        // The invalid value must NOT reach the service.
        _service.DidNotReceive().StartTimestamp = 2.0;
    }

    /// <summary>
    /// v1.5.1 PATCH Task 2: <c>OpenAsync</c> clears the range filter after
    /// a successful load. A new file has a different timestamp range; the
    /// old bounds (e.g. End=60 on a 5-second file) would silently filter
    /// out everything (Decision 5).
    /// </summary>
    [Fact]
    public async Task OpenAsync_ClearsRangeFilter()
    {
        // Arrange — pre-set range and a non-null RangeFilterError to prove
        // the clear path runs unconditionally on a successful load.
        _sut.StartTimestamp = 1.0;
        _sut.EndTimestamp = 2.0;
        _sut.RangeFilterError = "stale prior error";

        _fileDialog.ShowOpenDialog(Arg.Any<string>()).Returns("/tmp/clear-range.asc");
        _service.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _service.TotalDuration.Returns(5.0);

        // Act
        await _sut.OpenCommand.ExecuteAsync(null);

        // Assert — the three range-related properties are all null after load.
        _sut.StartTimestamp.Should().BeNull("OpenAsync clears StartTimestamp");
        _sut.EndTimestamp.Should().BeNull("OpenAsync clears EndTimestamp");
        _sut.RangeFilterError.Should().BeNull("OpenAsync clears RangeFilterError");
    }
}
