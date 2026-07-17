using System.IO;
using System.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OxyPlot;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using Xunit;
using PeakCan.Host.App.Services.AnalysisApiKey;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// v3.6.0 MINOR T2: pins the six core behaviors of
/// <see cref="TraceSessionAutoSaver"/>:
/// <list type="number">
/// <item>happy-path save writes to the auto-save location;</item>
/// <item>empty VM short-circuits with <c>false</c> (no write);</item>
/// <item>missing file → <see cref="AutoLoadResult.None"/>;</item>
/// <item>round-trip loads the same DTO that was written;</item>
/// <item>"No" persists the <see cref="AutoSavePrefs.NeverRestore"/>
/// flag and skips apply;</item>
/// <item>a subsequent call when <c>NeverRestore=true</c> suppresses
/// the prompt entirely.</item>
/// </list>
/// Each test uses a per-test temp directory under
/// <see cref="Path.GetTempPath"/> so parallel test execution is safe.
/// </summary>
public sealed class TraceSessionAutoSaverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _files = new();

    public TraceSessionAutoSaverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"autosave-{Guid.NewGuid():N}");
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
    }

    private string NewAutoSavePath() =>
        Track(Path.Combine(_tempDir, $"auto-{Guid.NewGuid():N}.tmtrace"));

    private string Track(string p) { _files.Add(p); return p; }

    // Minimal fake registry: empty source list by default; tests that
    // need a populated VM use WithFakeSource below.
    private static ITraceSessionRegistry MakeRegistryWith(TraceSource? src)
    {
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(src is null
            ? new List<TraceSource>()
            : new List<TraceSource> { src });
        return registry;
    }

    private static TraceViewerViewModel MakeVm(
        ITraceSessionRegistry registry,
        TraceSessionLibrary library)
    {
        var dbc = Substitute.For<DbcService>(Substitute.For<ILogger<DbcService>>());
        var logger = NullLogger<TraceViewerViewModel>.Instance;
        return new TraceViewerViewModel(
            registry, dbc, logger, library, fileDialog: null,
            apiKeyManager: Substitute.For<PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager>());
    }

    private static TraceSessionLibrary MakeLib(string path) =>
        new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance);

    private static TraceSessionAutoSaver MakeSaver(
        string autoSavePath,
        ITraceSessionRegistry registry,
        IAutoSavePrefsStore prefs,
        IMessageBoxPrompt prompt)
    {
        var library = MakeLib(autoSavePath);
        var vm = MakeVm(registry, library);
        var provider = Substitute.For<ITraceViewerViewModelProvider>();
        provider.GetCurrent().Returns(vm);
        return new TraceSessionAutoSaver(
            provider, library, prefs, prompt,
            NullLogger<TraceSessionAutoSaver>.Instance,
            autoSavePath);
    }

    // InMemoryPrefsStore now lives in its own file (Services/Trace/InMemoryPrefsStore.cs,
    // v3.7.0 PATCH). The factory and tests below reference it by bare name
    // (same namespace).

    [Fact]
    public async Task TrySaveAutoSnapshotAsync_WritesToAppDataLocation()
    {
        // Arrange
        var path = NewAutoSavePath();
        var src = new TraceSource("src1", "highway", @"C:/r.asc", OxyColors.Red);
        var registry = MakeRegistryWith(src);
        var prefs = new InMemoryPrefsStore();
        var prompt = Substitute.For<IMessageBoxPrompt>();
        var sut = MakeSaver(path, registry, prefs, prompt);

        // Act
        var wrote = await sut.TrySaveAutoSnapshotAsync(CancellationToken.None);

        // Assert
        wrote.Should().BeTrue();
        File.Exists(path).Should().BeTrue("the auto-save file must exist after a successful write");
        var loaded = MakeLib(path).Load(path);
        loaded.Should().NotBeNull();
        loaded!.Sources.Should().HaveCount(1);
        loaded.Sources[0].DisplayName.Should().Be("highway");
    }

    [Fact]
    public async Task TrySaveAutoSnapshotAsync_WithNoSources_ReturnsFalse()
    {
        // Arrange — empty registry (zero sources).
        var path = NewAutoSavePath();
        var registry = MakeRegistryWith(src: null);
        var prefs = new InMemoryPrefsStore();
        var prompt = Substitute.For<IMessageBoxPrompt>();
        var sut = MakeSaver(path, registry, prefs, prompt);

        // Act
        var wrote = await sut.TrySaveAutoSnapshotAsync(CancellationToken.None);

        // Assert
        wrote.Should().BeFalse("an empty session has nothing worth persisting");
        File.Exists(path).Should().BeFalse("we must NOT create a zero-source file");
    }

    [Fact]
    public async Task TryLoadAutoSnapshotAsync_ReturnsNullWhenFileMissing()
    {
        // Arrange — path does not exist on disk.
        var path = NewAutoSavePath();
        var registry = MakeRegistryWith(src: null);
        var prefs = new InMemoryPrefsStore();
        var prompt = Substitute.For<IMessageBoxPrompt>();
        var sut = MakeSaver(path, registry, prefs, prompt);

        // Act
        var result = await sut.TryLoadAutoSnapshotAsync(CancellationToken.None);

        // Assert
        result.Dto.Should().BeNull();
        result.SourceFile.Should().BeEmpty();
        result.Should().Be(AutoLoadResult.None);
    }

    [Fact]
    public async Task TryLoadAutoSnapshotAsync_RoundTripsDtoFromVm()
    {
        // Arrange — write a bundle via the saver, then load it back.
        var path = NewAutoSavePath();
        var src = new TraceSource(
            "srcA", "drive_downtown", @"C:/rec.asc",
            OxyColor.FromArgb(255, 0x12, 0x34, 0x56));
        var registry = MakeRegistryWith(src);
        var prefs = new InMemoryPrefsStore();
        var prompt = Substitute.For<IMessageBoxPrompt>();
        var sut = MakeSaver(path, registry, prefs, prompt);
        (await sut.TrySaveAutoSnapshotAsync(CancellationToken.None)).Should().BeTrue();

        // Act
        var result = await sut.TryLoadAutoSnapshotAsync(CancellationToken.None);

        // Assert
        result.Dto.Should().NotBeNull();
        result.SourceFile.Should().Be(path);
        result.SavedAt.Should().NotBe(DateTimeOffset.MinValue);
        result.Dto!.Sources.Should().HaveCount(1);
        var loadedSource = result.Dto.Sources[0];
        loadedSource.DisplayName.Should().Be("drive_downtown");
        loadedSource.Path.Should().Be(@"C:/rec.asc");
        loadedSource.ColorA.Should().Be(255);
        loadedSource.ColorR.Should().Be(0x12);
        loadedSource.ColorG.Should().Be(0x34);
        loadedSource.ColorB.Should().Be(0x56);
    }

    [Fact]
    public async Task ApplyAutoSnapshotAsync_UserSaysNo_PersistsNeverRestoreFlag()
    {
        // Arrange — bundle exists; user answers No.
        var path = NewAutoSavePath();
        var src = new TraceSource("src1", "trip", @"C:/t.asc", OxyColors.Green);
        var registry = MakeRegistryWith(src);
        var prefs = new InMemoryPrefsStore();
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
            .Returns(MessageBoxResult.No);
        var sut = MakeSaver(path, registry, prefs, prompt);
        // Need a real VM with the bundle on disk for Apply to load it.
        (await sut.TrySaveAutoSnapshotAsync(CancellationToken.None)).Should().BeTrue();
        var vm = registry.Sources.Count > 0
            ? MakeVm(registry, MakeLib(path))
            : throw new InvalidOperationException();

        // Act
        var outcome = await sut.ApplyAutoSnapshotAsync(vm, CancellationToken.None);

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
        // Arrange — prefs already say NeverRestore=true. Bundle on disk
        // exists, but the prompt must be suppressed.
        var path = NewAutoSavePath();
        var src = new TraceSource("src1", "trip", @"C:/t.asc", OxyColors.Green);
        var registry = MakeRegistryWith(src);
        var prefs = new InMemoryPrefsStore { Current = new AutoSavePrefs(NeverRestore: true) };
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
            .Returns(MessageBoxResult.Yes);
        var sut = MakeSaver(path, registry, prefs, prompt);
        (await sut.TrySaveAutoSnapshotAsync(CancellationToken.None)).Should().BeTrue();
        var vm = MakeVm(registry, MakeLib(path));

        // Act
        var outcome = await sut.ApplyAutoSnapshotAsync(vm, CancellationToken.None);

        // Assert
        outcome.Applied.Should().BeFalse();
        outcome.PromptShown.Should().BeFalse();
        outcome.Answer.Should().Be(RestoreAnswer.NeverRestore);
        await prompt.DidNotReceiveWithAnyArgs().ShowAsync(default!, default!, default);
    }
}