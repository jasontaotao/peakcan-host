using System.IO;
using System.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// v3.7.0 MINOR Chunk 3: tests for <see cref="ReplaySessionAutoSaver"/>.
/// Mirrors <see cref="TraceSessionAutoSaverTests"/> shape (6 tests)
/// but with a <see cref="ReplayViewModel"/> stub instead of Trace.
/// </summary>
public sealed class ReplaySessionAutoSaverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _files = new();

    public ReplaySessionAutoSaverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"replay-autosave-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in _files)
                if (File.Exists(f)) File.Delete(f);
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string NewAutoSavePath() =>
        Track(Path.Combine(_tempDir, $"replay-{Guid.NewGuid():N}.tmtrace"));

    private string Track(string p) { _files.Add(p); return p; }

    private (ReplaySessionAutoSaver Saver, IReplayViewModelProvider Provider, ReplayViewModel Vm)
        MakeSaver(string path, IAutoSavePrefsStore prefs, IMessageBoxPrompt prompt)
    {
        var library = new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance);
        var vm = new ReplayViewModel(
            Substitute.For<IReplayService>(),
            Substitute.For<IFileDialogService>(),
            Substitute.For<IAscContentHasher>(),
            Substitute.For<IAscLocator>(),
            library,
            new RecentSessionsService(NullLogger<RecentSessionsService>.Instance,
                Path.Combine(_tempDir, $"recent-{Guid.NewGuid():N}.json")));
        // Pre-set LoadedFilePath so the saver's early-out is skipped.
        typeof(ReplayViewModel).GetProperty("LoadedFilePath")!
            .SetValue(vm, @"C:/replay.asc");
        var provider = Substitute.For<IReplayViewModelProvider>();
        provider.GetCurrent().Returns(vm);
        return (new ReplaySessionAutoSaver(
            provider, library, prefs, prompt,
            NullLogger<ReplaySessionAutoSaver>.Instance, path),
            provider, vm);
    }

    private (ReplaySessionAutoSaver Saver, IReplayViewModelProvider Provider, ReplayViewModel Vm)
        MakeSaver(string path) =>
        MakeSaver(path, new InMemoryPrefsStore(), Substitute.For<IMessageBoxPrompt>());

    [Fact]
    public async Task TrySaveAutoSnapshotAsync_WritesToAppDataLocation()
    {
        // arrange
        var path = NewAutoSavePath();
        var (saver, _, _) = MakeSaver(path);

        // act
        var wrote = await saver.TrySaveAutoSnapshotAsync(CancellationToken.None);

        // assert
        wrote.Should().BeTrue();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task TrySaveAutoSnapshotAsync_NoLoadedFile_ReturnsFalse()
    {
        // arrange — VM has no LoadedFilePath (the make-saver sets it; reset here)
        var path = NewAutoSavePath();
        var (saver, _, vm) = MakeSaver(path);
        typeof(ReplayViewModel).GetProperty("LoadedFilePath")!
            .SetValue(vm, null);

        // act
        var wrote = await saver.TrySaveAutoSnapshotAsync(CancellationToken.None);

        // assert
        wrote.Should().BeFalse();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task TryLoadAutoSnapshotAsync_ReturnsNullWhenFileMissing()
    {
        // arrange — saver constructed with a path that does not exist
        var missingPath = Path.Combine(_tempDir, "never-created.tmtrace");
        var library = new TraceSessionLibrary(missingPath, NullLogger<TraceSessionLibrary>.Instance);
        var provider = Substitute.For<IReplayViewModelProvider>();
        var saver = new ReplaySessionAutoSaver(
            provider, library,
            new InMemoryPrefsStore(),
            Substitute.For<IMessageBoxPrompt>(),
            NullLogger<ReplaySessionAutoSaver>.Instance, missingPath);

        // act
        var result = await saver.TryLoadAutoSnapshotAsync(CancellationToken.None);

        // assert
        result.Dto.Should().BeNull();
    }

    [Fact]
    public async Task TryLoadAutoSnapshotAsync_RoundTripsDtoFromVm()
    {
        // arrange — save + reload
        var path = NewAutoSavePath();
        var (saver, _, _) = MakeSaver(path);
        await saver.TrySaveAutoSnapshotAsync(CancellationToken.None);

        // act
        var loaded = await saver.TryLoadAutoSnapshotAsync(CancellationToken.None);

        // assert
        loaded.Dto.Should().NotBeNull();
        loaded.Dto!.Sources.Should().HaveCount(1);
        loaded.Dto.Sources[0].Path.Should().Be(@"C:/replay.asc");
    }

    [Fact]
    public async Task ApplyAutoSnapshotAsync_UserSaysNo_PersistsNeverRestoreFlag()
    {
        // Arrange — bundle exists; user answers No.
        var path = NewAutoSavePath();
        var prefs = new InMemoryPrefsStore();
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
            .Returns(MessageBoxResult.No);
        var (saver, _, vm) = MakeSaver(path, prefs, prompt);
        (await saver.TrySaveAutoSnapshotAsync(CancellationToken.None)).Should().BeTrue();

        // Act
        var outcome = await saver.ApplyAutoSnapshotAsync(vm, CancellationToken.None);

        // Assert
        outcome.Applied.Should().BeFalse();
        outcome.PromptShown.Should().BeTrue();
        outcome.Answer.Should().Be(RestoreAnswer.No);
        prefs.Current.NeverRestore.Should().BeTrue(
            "user said No → opt-out flag persists so we never prompt again");
    }

    [Fact]
    public async Task ApplyAutoSnapshotAsync_AfterNeverRestore_NoPrompt()
    {
        // Arrange — prefs already say NeverRestore=true; bundle exists but
        // the prompt must be suppressed.
        var path = NewAutoSavePath();
        var prefs = new InMemoryPrefsStore { Current = new AutoSavePrefs(NeverRestore: true) };
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
            .Returns(MessageBoxResult.Yes);
        var (saver, _, vm) = MakeSaver(path, prefs, prompt);
        (await saver.TrySaveAutoSnapshotAsync(CancellationToken.None)).Should().BeTrue();

        // Act
        var outcome = await saver.ApplyAutoSnapshotAsync(vm, CancellationToken.None);

        // Assert
        outcome.Applied.Should().BeFalse();
        outcome.PromptShown.Should().BeFalse();
        outcome.Answer.Should().Be(RestoreAnswer.NeverRestore);
        await prompt.DidNotReceiveWithAnyArgs().ShowAsync(default!, default!, default);
    }
}
