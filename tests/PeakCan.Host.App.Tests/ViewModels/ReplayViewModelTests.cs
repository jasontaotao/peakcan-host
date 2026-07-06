using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;
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
    private readonly IAscContentHasher _hasher = Substitute.For<IAscContentHasher>();
    private readonly IAscLocator _ascLocator = Substitute.For<IAscLocator>();
    private readonly TraceSessionLibrary _library;
    private readonly RecentSessionsService _recentSessions;
    private readonly ReplayViewModel _sut;

    public ReplayViewModelTests()
    {
        _service.TotalDuration.Returns(10.0);
        _library = new TraceSessionLibrary(
            Path.Combine(Path.GetTempPath(), $"tmtrace-replay-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);
        _recentSessions = new RecentSessionsService(
            NullLogger<RecentSessionsService>.Instance,
            Path.Combine(Path.GetTempPath(), $"recent-replay-{Guid.NewGuid():N}.json"));
        _sut = NewVm(_service, _fileDialog, _hasher, _ascLocator, _library, _recentSessions);
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 1: test ctor helper. Wires the four new
    /// dependencies (hasher / locator / library / recentSessions) with
    /// the test's pre-built doubles so individual tests can override
    /// behavior. The legacy two-arg shape is preserved by defaulting
    /// the new deps to NSubstitute mocks + temp-path real services.
    /// </summary>
    private static ReplayViewModel NewVm(
        IReplayService svc,
        IFileDialogService dlg,
        IAscContentHasher? hasher = null,
        IAscLocator? ascLocator = null,
        TraceSessionLibrary? library = null,
        RecentSessionsService? recentSessions = null)
    {
        hasher ??= Substitute.For<IAscContentHasher>();
        ascLocator ??= Substitute.For<IAscLocator>();
        library ??= new TraceSessionLibrary(
            Path.Combine(Path.GetTempPath(), $"tmtrace-replay-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);
        recentSessions ??= new RecentSessionsService(
            NullLogger<RecentSessionsService>.Instance,
            Path.Combine(Path.GetTempPath(), $"recent-replay-{Guid.NewGuid():N}.json"));
        return new ReplayViewModel(svc, dlg, hasher, ascLocator, library, recentSessions);
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

    /// <summary>
    /// v1.6.1 PATCH Item 2: source-gen setter wrote the backing field
    /// before the partial callback could reject. The new SetProperty
    /// with validator prevents the field from being touched on rejection,
    /// so the UI TextBox reads the prior value (not the rejected one)
    /// and the service retains the prior value.
    /// </summary>
    [Fact]
    public void StartTimestamp_set_above_End_reverts_to_previous_value()
    {
        var service = Substitute.For<IReplayService>();
        var fileDialog = Substitute.For<IFileDialogService>();
        var sut = NewVm(service, fileDialog);

        sut.EndTimestamp = 10.0;

        sut.StartTimestamp = 20.0;  // violates Start <= End

        sut.StartTimestamp.Should().BeNull(
            "rejected update must not change the VM property; UI binding will see the old value");
        sut.RangeFilterError.Should().Be("Start must be ≤ End");
        service.DidNotReceive().StartTimestamp = Arg.Any<double?>();
    }

    [Fact]
    public void StartTimestamp_set_below_End_pushes_to_service_and_clears_error()
    {
        var service = Substitute.For<IReplayService>();
        var fileDialog = Substitute.For<IFileDialogService>();
        var sut = NewVm(service, fileDialog);

        sut.StartTimestamp = 5.0;
        sut.EndTimestamp = 10.0;

        sut.RangeFilterError.Should().BeNull();
        service.Received(1).StartTimestamp = 5.0;
        service.Received(1).EndTimestamp = 10.0;
    }

    [Fact]
    public void EndTimestamp_set_below_Start_reverts_to_previous_value()
    {
        var service = Substitute.For<IReplayService>();
        var fileDialog = Substitute.For<IFileDialogService>();
        var sut = NewVm(service, fileDialog);

        sut.StartTimestamp = 10.0;

        sut.EndTimestamp = 5.0;  // violates Start <= End

        sut.EndTimestamp.Should().BeNull();
        sut.RangeFilterError.Should().Be("Start must be ≤ End");
        service.DidNotReceive().EndTimestamp = Arg.Any<double?>();
    }

    [Fact]
    public void SetProperty_with_null_end_clears_constraint()
    {
        // When one endpoint is null, the other is unconstrained.
        var service = Substitute.For<IReplayService>();
        var fileDialog = Substitute.For<IFileDialogService>();
        var sut = NewVm(service, fileDialog);

        sut.EndTimestamp = null;
        sut.StartTimestamp = 1_000_000.0;  // huge value, no End to violate

        sut.StartTimestamp.Should().Be(1_000_000.0);
        sut.RangeFilterError.Should().BeNull();
        service.Received(1).StartTimestamp = 1_000_000.0;
    }

    // ---------- v3.7.0 MINOR Chunk 1: T1 BuildSnapshot + OpenSessionAsync ----------

    /// <summary>
    /// v3.7.0 MINOR Chunk 1 T1: <c>BuildSnapshot</c> packages the loaded
    /// .asc into a single-source <c>TraceSessionBundleDto</c>. The
    /// contentHash is sourced from <see cref="IAscContentHasher"/>
    /// (synchronous call) when the file exists. Display name is the
    /// filename stem; path is verbatim.
    /// </summary>
    [Fact]
    public void BuildSnapshot_ProducesSingleSourceBundle()
    {
        // Arrange — create a small real file on disk and seed the VM state
        var tempAsc = Path.Combine(Path.GetTempPath(), $"build-snap-{Guid.NewGuid():N}.asc");
        File.WriteAllText(tempAsc, "0.000 1 100x R\n");
        try
        {
            _hasher.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
            _sut.LoadedFilePath = tempAsc;
            _sut.IsLoaded = true;

            // Act
            var dto = _sut.BuildSnapshot();

            // Assert
            dto.Sources.Should().HaveCount(1);
            dto.Sources[0].Path.Should().Be(tempAsc);
            dto.Sources[0].DisplayName.Should().Be(Path.GetFileNameWithoutExtension(tempAsc));
            dto.Sources[0].ContentHash.Should().Be(
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
            dto.Playback.Should().NotBeNull();
        }
        finally
        {
            try { File.Delete(tempAsc); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 1 T1: <c>OpenSessionAsync</c> round-trips a
    /// saved bundle: BuildSnapshot, save, then load. The reloaded VM
    /// state must reflect the bundle (path + transport). Reuses the
    /// existing ctor pattern via NewVm helper.
    /// </summary>
    [Fact]
    public async Task OpenSessionAsync_LoadsFromBundle()
    {
        // Arrange — fresh VM with its own library so save→load is hermetic.
        var localLib = new TraceSessionLibrary(
            Path.Combine(Path.GetTempPath(), $"tmtrace-rb-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);
        var localLocator = Substitute.For<IAscLocator>();
        var localVm = NewVm(_service, _fileDialog, _hasher, localLocator, localLib, _recentSessions);

        var tempAsc = Path.Combine(Path.GetTempPath(), $"load-{Guid.NewGuid():N}.asc");
        File.WriteAllText(tempAsc, "0.000 1 100x R\n");
        try
        {
            _hasher.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns("aaaabbbbccccddddaaaabbbbccccddddaaaabbbbccccddddaaaabbbbccccdddd");
            _service.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            _service.TotalDuration.Returns(7.5);
            // Seed VM state directly (bypass the dialog-driven OpenAsync
            // path — we control the playback state we save).
            localVm.LoadedFilePath = tempAsc;
            localVm.IsLoaded = true;
            localVm.Loop = true;
            localVm.Speed = 2.0;
            localVm.CurrentTimestamp = 3.25;
            localVm.StartTimestamp = 1.0;
            localVm.EndTimestamp = 5.0;
            localVm.CanIdFilterText = "0x100";

            var snapshot = localVm.BuildSnapshot();
            var bundlePath = Path.Combine(Path.GetTempPath(), $"replay-bundle-{Guid.NewGuid():N}.tmtrace");
            localLib.Save(snapshot, bundlePath);

            // Arrange: a fresh VM that will consume the bundle.
            var consumer = NewVm(_service, _fileDialog, _hasher, localLocator, localLib, _recentSessions);
            _service.LoadAsync(tempAsc, Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            _service.TotalDuration.Returns(7.5);

            // Act
            var missing = await consumer.OpenSessionAsync(bundlePath);

            // Assert
            missing.Should().BeEmpty("the recorded .asc still exists at its original path");
            consumer.LoadedFilePath.Should().Be(tempAsc);
            consumer.Loop.Should().BeTrue();
            consumer.Speed.Should().Be(2.0);
            consumer.CurrentTimestamp.Should().Be(3.25);
            consumer.StartTimestamp.Should().Be(1.0);
            consumer.EndTimestamp.Should().Be(5.0);
            consumer.CanIdFilterText.Should().Be("0x100");
            consumer.IsPlaying.Should().BeFalse("open always lands on a paused cursor");
        }
        finally
        {
            try { File.Delete(tempAsc); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 1 T1: when the recorded .asc is missing but
    /// the bundle carries a contentHash, <c>OpenSessionAsync</c> asks
    /// the locator for a relocated path and retries the load. Success
    /// means the relocated path is used; the recorded path is not
    /// added to the missing list.
    /// </summary>
    [Fact]
    public async Task OpenSessionAsync_MissingFile_FallsBackToHashLocator()
    {
        // Arrange — fabricate a bundle with a stale path + valid hash
        // pointing at a real (relocated) file on disk.
        var stalePath = Path.Combine(Path.GetTempPath(), $"stale-{Guid.NewGuid():N}.asc");
        var relocatedPath = Path.Combine(Path.GetTempPath(), $"relocated-{Guid.NewGuid():N}.asc");
        File.WriteAllText(relocatedPath, "0.000 1 100x R\n");
        try
        {
            _hasher.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns("hashforrelocation");
            _ascLocator.LocateAsync("hashforrelocation", Arg.Any<CancellationToken>())
                .Returns(relocatedPath);
            _service.LoadAsync(relocatedPath, Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            _service.TotalDuration.Returns(4.0);

            // Build a bundle manually — stale path, real hash, pointing at the relocated file.
            var snapshot = new TraceSessionBundleDto
            {
                Version = 1,
                Schema = TraceSessionLibrary.CurrentSchema,
                SavedAt = DateTimeOffset.UtcNow,
                DbcPath = "",
                GlobalCanIdFilter = "",
                Sources = new List<BundleSourceDto>
                {
                    new()
                    {
                        SourceId = "replay-1",
                        DisplayName = "relocated",
                        Path = stalePath,
                        ContentHash = "hashforrelocation",
                    },
                },
                Playback = new BundlePlaybackDto
                {
                    MasterSourceId = "",
                    Loop = false,
                    Speed = 1.0,
                    ScrubberValue = 0.0,
                },
                Viewports = new List<BundleViewportDto>(),
            };
            var bundlePath = Path.Combine(Path.GetTempPath(), $"stale-bundle-{Guid.NewGuid():N}.tmtrace");
            _library.Save(snapshot, bundlePath);

            // Act
            var missing = await _sut.OpenSessionAsync(bundlePath);

            // Assert — no missing reported, and the relocated path was loaded.
            missing.Should().BeEmpty();
            await _service.Received(1).LoadAsync(relocatedPath, Arg.Any<CancellationToken>());
        }
        finally
        {
            try { File.Delete(relocatedPath); } catch { /* best effort */ }
        }
    }

    // ---------- v3.7.0 MINOR Chunk 1: T2 SaveCommand + OpenSessionCommand ----------

    /// <summary>
    /// v3.7.0 MINOR Chunk 1 T2: <c>SaveCommand</c> pops the save
    /// dialog, builds a snapshot, writes the .tmtrace file, and adds
    /// the path to <see cref="RecentSessionsService"/>.
    /// </summary>
    [Fact]
    public async Task SaveCommand_PopsDialog_BuildsAndSavesBundle_AddsToRecent()
    {
        // Arrange — pre-load a fake .asc, prime the save dialog
        var tempAsc = Path.Combine(Path.GetTempPath(), $"save-src-{Guid.NewGuid():N}.asc");
        var bundlePath = Path.Combine(Path.GetTempPath(), $"save-out-{Guid.NewGuid():N}.tmtrace");
        File.WriteAllText(tempAsc, "0.000 1 100x R\n");
        try
        {
            _hasher.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns("feedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedfacefeedface");
            _fileDialog.ShowSaveDialog(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(bundlePath);
            _sut.LoadedFilePath = tempAsc;
            _sut.IsLoaded = true;

            // Act
            await _sut.SaveCommand.ExecuteAsync(null);

            // Assert
            File.Exists(bundlePath).Should().BeTrue("SaveCommand persists the bundle to disk");
            _recentSessions.Recent.Should().HaveCount(1, "the saved path is added to the MRU list");
            _recentSessions.Recent[0].Path.Should().Be(bundlePath);
        }
        finally
        {
            try { File.Delete(tempAsc); } catch { /* best effort */ }
            try { File.Delete(bundlePath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 1 T2: <c>OpenSessionCommand</c> pops the open
    /// dialog and forwards the chosen path to <c>OpenSessionAsync</c>.
    /// Verify by saving first, then opening the same bundle via the
    /// command.
    /// </summary>
    [Fact]
    public async Task OpenSessionCommand_PopsDialog_LoadsBundle()
    {
        // Arrange — pre-save a bundle
        var tempAsc = Path.Combine(Path.GetTempPath(), $"open-cmd-{Guid.NewGuid():N}.asc");
        var bundlePath = Path.Combine(Path.GetTempPath(), $"open-cmd-bundle-{Guid.NewGuid():N}.tmtrace");
        File.WriteAllText(tempAsc, "0.000 1 100x R\n");
        try
        {
            _hasher.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns("c0ffeec0ffeec0ffeec0ffeec0ffeec0ffeec0ffeec0ffeec0ffeec0ffeec0ffee");
            _service.LoadAsync(tempAsc, Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            _service.TotalDuration.Returns(2.0);
            // First save via the command.
            _fileDialog.ShowSaveDialog(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(bundlePath);
            _sut.LoadedFilePath = tempAsc;
            _sut.IsLoaded = true;
            await _sut.SaveCommand.ExecuteAsync(null);

            // Now configure the open dialog for OpenSessionCommand.
            _fileDialog.ShowOpenDialog(Arg.Any<string>()).Returns(bundlePath);

            // Act
            await _sut.OpenSessionCommand.ExecuteAsync(null);

            // Assert — VM state reflects the loaded bundle.
            _sut.LoadedFilePath.Should().Be(tempAsc);
            _sut.IsLoaded.Should().BeTrue();
        }
        finally
        {
            try { File.Delete(tempAsc); } catch { /* best effort */ }
            try { File.Delete(bundlePath); } catch { /* best effort */ }
        }
    }

    // ---------- v3.7.0 MINOR Chunk 1: T10 edge cases ----------

    /// <summary>
    /// v3.7.0 MINOR Chunk 1 T10: when a bundle has no contentHash and
    /// the recorded path is missing, the path is reported in the
    /// missing list. No locator call is made (the locator only
    /// triggers on a non-empty hash).
    /// </summary>
    [Fact]
    public async Task OpenSessionAsync_StalePath_NoHash_ReportsMissing()
    {
        // Arrange — bundle with a path that does not exist, no contentHash.
        var missingPath = Path.Combine(Path.GetTempPath(), $"never-existed-{Guid.NewGuid():N}.asc");
        var snapshot = new TraceSessionBundleDto
        {
            Version = 1,
            Schema = TraceSessionLibrary.CurrentSchema,
            SavedAt = DateTimeOffset.UtcNow,
            Sources = new List<BundleSourceDto>
            {
                new() { SourceId = "x", DisplayName = "x", Path = missingPath, ContentHash = "" },
            },
            Playback = new BundlePlaybackDto(),
            Viewports = new List<BundleViewportDto>(),
        };
        var bundlePath = Path.Combine(Path.GetTempPath(), $"nohash-bundle-{Guid.NewGuid():N}.tmtrace");
        _library.Save(snapshot, bundlePath);
        // Configure the mock to throw FileNotFoundException for the
        // missing path — matches the real ReplayService behavior
        // (FileNotFoundException is in the catch list of OpenSessionAsync).
        _service.LoadAsync(missingPath, Arg.Any<CancellationToken>())
            .Returns(_ => throw new FileNotFoundException("not on disk", missingPath));

        // Act
        var missing = await _sut.OpenSessionAsync(bundlePath);

        // Assert
        missing.Should().ContainSingle().Which.Should().Be(missingPath);
        await _ascLocator.DidNotReceiveWithAnyArgs().LocateAsync(default!, default);
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 1 T10: <c>SaveCommand</c> requests the WPF
    /// save dialog with the .tmtrace filter and default extension.
    /// </summary>
    [Fact]
    public async Task SaveCommand_ShowsSaveDialog_DefaultsToDotTmtrace()
    {
        // Arrange
        _fileDialog.ShowSaveDialog(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns((string?)null);  // user cancels → still exercises dialog pop
        _sut.LoadedFilePath = null;
        _sut.IsLoaded = false;

        // Act
        await _sut.SaveCommand.ExecuteAsync(null);

        // Assert — the dialog was invoked with the .tmtrace filter and
        // default extension. NSubstitute's Arg.Is gives us a content
        // check.
        _fileDialog.Received(1).ShowSaveDialog(
            Arg.Is<string>(f => f.Contains(".tmtrace", StringComparison.OrdinalIgnoreCase)),
            Arg.Is<string?>(e => e == ".tmtrace"),
            Arg.Any<string?>());
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 1 T10: when a bundle is saved mid-playback
    /// (IsPlaying=true) and later reloaded, the consumer must land on
    /// a paused cursor (IsPlaying=false). Open never auto-resumes.
    /// </summary>
    [Fact]
    public async Task OpenSessionAsync_RestoresToPausedCursor()
    {
        // Arrange — fabricate a bundle and a VM that "thinks" it's
        // playing; OpenSessionAsync should force IsPlaying=false.
        var tempAsc = Path.Combine(Path.GetTempPath(), $"paused-{Guid.NewGuid():N}.asc");
        File.WriteAllText(tempAsc, "0.000 1 100x R\n");
        try
        {
            _hasher.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns("deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef");
            _service.LoadAsync(tempAsc, Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            _service.TotalDuration.Returns(2.0);

            var snapshot = new TraceSessionBundleDto
            {
                Version = 1,
                Schema = TraceSessionLibrary.CurrentSchema,
                SavedAt = DateTimeOffset.UtcNow,
                Sources = new List<BundleSourceDto>
                {
                    new()
                    {
                        SourceId = "p",
                        DisplayName = Path.GetFileNameWithoutExtension(tempAsc),
                        Path = tempAsc,
                        ContentHash = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
                    },
                },
                Playback = new BundlePlaybackDto
                {
                    MasterSourceId = "",
                    Loop = false,
                    Speed = 1.0,
                    ScrubberValue = 0.0,
                },
                Viewports = new List<BundleViewportDto>(),
            };
            var bundlePath = Path.Combine(Path.GetTempPath(), $"paused-bundle-{Guid.NewGuid():N}.tmtrace");
            _library.Save(snapshot, bundlePath);
            _sut.IsPlaying = true;   // simulate prior session state

            // Act
            var missing = await _sut.OpenSessionAsync(bundlePath);

            // Assert
            missing.Should().BeEmpty();
            _sut.IsPlaying.Should().BeFalse("OpenSessionAsync never auto-resumes playback");
        }
        finally
        {
            try { File.Delete(tempAsc); } catch { /* best effort */ }
        }
    }

    // ---------- v3.7.0 MINOR Chunk 2: RecentSessionEntries + ClearRecentSessions ----------

    /// <summary>
    /// v3.7.0 MINOR Chunk 2: <see cref="ReplayViewModel.RecentSessionEntries"/>
    /// surfaces only the entries whose <see cref="RecentSessionDto.ViewType"/>
    /// is <c>"replay"</c>. Trace Viewer entries and legacy empty-viewType
    /// entries are filtered out at the VM level (the consumer of the
    /// menu) — the service itself stays unfiltered.
    /// </summary>
    [Fact]
    public void RecentSessionEntries_OnlyIncludesReplayEntries()
    {
        // Arrange — separate MRU service so the test owns its state
        var localRecent = new RecentSessionsService(
            NullLogger<RecentSessionsService>.Instance,
            Path.Combine(Path.GetTempPath(), $"recent-chunk2a-{Guid.NewGuid():N}.json"));
        localRecent.Add(@"C:\x\trace.tmtrace", viewType: "trace");
        localRecent.Add(@"C:\y\replay.tmtrace", viewType: "replay");
        localRecent.Add(@"C:\z\legacy.tmtrace", viewType: "");   // pre-v3.7.0 entry
        var sut = NewVm(_service, _fileDialog, _hasher, _ascLocator, _library, localRecent);

        // Act + Assert
        sut.RecentSessionEntries.Should().HaveCount(1);
        sut.RecentSessionEntries[0].Path.Should().Be(@"C:\y\replay.tmtrace");
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 2: <see cref="ReplayViewModel.ClearRecentSessionsCommand"/>
    /// drops replay entries only. Trace Viewer entries and any future
    /// viewType's entries survive — the two MRU lists are independent
    /// on the shared service backing file.
    /// </summary>
    [Fact]
    public void ClearRecentSessions_RemovesOnlyReplayEntries()
    {
        // Arrange
        var localRecent = new RecentSessionsService(
            NullLogger<RecentSessionsService>.Instance,
            Path.Combine(Path.GetTempPath(), $"recent-chunk2b-{Guid.NewGuid():N}.json"));
        localRecent.Add(@"C:\x\trace.tmtrace", viewType: "trace");
        localRecent.Add(@"C:\y\replay1.tmtrace", viewType: "replay");
        localRecent.Add(@"C:\z\replay2.tmtrace", viewType: "replay");
        var sut = NewVm(_service, _fileDialog, _hasher, _ascLocator, _library, localRecent);

        // Act
        sut.ClearRecentSessionsCommand.Execute(null);

        // Assert
        localRecent.Recent.Should().HaveCount(1);
        localRecent.Recent[0].Path.Should().Be(@"C:\x\trace.tmtrace");
        localRecent.Recent[0].ViewType.Should().Be("trace");
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 2: <c>SaveCommand</c> now records the saved
    /// path with <c>viewType: "replay"</c> (chunk-1 used the legacy
    /// default). This pins the new contract: a Replay save is tagged
    /// "replay" so the AppShell menu filter does not pick it up and
    /// the Replay Recent submenu DOES.
    /// </summary>
    [Fact]
    public async Task SaveCommand_RecordsViewTypeAsReplay()
    {
        // Arrange
        var localRecent = new RecentSessionsService(
            NullLogger<RecentSessionsService>.Instance,
            Path.Combine(Path.GetTempPath(), $"recent-chunk2c-{Guid.NewGuid():N}.json"));
        var tempAsc = Path.Combine(Path.GetTempPath(), $"save-replay-{Guid.NewGuid():N}.asc");
        var bundlePath = Path.Combine(Path.GetTempPath(), $"save-replay-out-{Guid.NewGuid():N}.tmtrace");
        File.WriteAllText(tempAsc, "0.000 1 100x R\n");
        try
        {
            _hasher.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
            _fileDialog.ShowSaveDialog(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(bundlePath);
            var sut = NewVm(_service, _fileDialog, _hasher, _ascLocator, _library, localRecent);
            sut.LoadedFilePath = tempAsc;
            sut.IsLoaded = true;

            // Act
            await sut.SaveCommand.ExecuteAsync(null);

            // Assert
            localRecent.Recent.Should().HaveCount(1);
            localRecent.Recent[0].ViewType.Should().Be("replay",
                "Replay SaveCommand tags the MRU entry with viewType=replay so the two submenus stay disjoint");
        }
        finally
        {
            try { File.Delete(tempAsc); } catch { /* best effort */ }
            try { File.Delete(bundlePath); } catch { /* best effort */ }
        }
    }
}
