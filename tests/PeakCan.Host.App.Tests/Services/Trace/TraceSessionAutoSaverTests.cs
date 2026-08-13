using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ScottPlot;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// v3.6.0 MINOR T2: pins the six core behaviors of
/// <see cref="TraceSessionAutoSaver"/>:
/// <list type="number">
/// <item>happy-path save writes to the auto-save location;</item>
/// <item>empty session short-circuits with <c>false</c> (no write);</item>
/// <item>missing file → <see cref="AutoLoadResult.None"/>;</item>
/// <item>round-trip loads the same DTO that was written;</item>
/// <item>"No" persists the <see cref="AutoSavePrefs.NeverRestore"/>
/// flag and skips apply;</item>
/// <item>a subsequent call when <c>NeverRestore=true</c> suppresses
/// the prompt entirely.</item>
/// </list>
/// v3.x (会话状态剥离 Task 4): auto-saver 改直连 <see cref="ITraceSessionService"/>，
/// 测试替身换成 service 替身（断言 BuildSnapshot / OpenSessionAsync 被调）。
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

    // v3.x (会话状态剥离 Task 4): 构造 ITraceSessionService 替身。HasContent 由
    // src 是否为空决定；BuildSnapshot 返回含该 src 的 bundle（DisplayName / Path /
    // 颜色字节与真实序列化形状一致，供 round-trip 断言）。
    private static ITraceSessionService MakeSessionWith(TraceSource? src)
    {
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        if (src is null)
        {
            session.HasContent.Returns(false);
        }
        else
        {
            session.HasContent.Returns(true);
            session.BuildSnapshot().Returns(new TraceSessionBundleDto
            {
                Sources = new List<BundleSourceDto>
                {
                    new()
                    {
                        SourceId = src.SourceId,
                        DisplayName = src.DisplayName,
                        Path = src.Path,
                        ColorA = src.Color.A,
                        ColorR = src.Color.R,
                        ColorG = src.Color.G,
                        ColorB = src.Color.B,
                        StrokeStyle = src.StrokeStyle.ToString() ?? "",
                    },
                },
            });
        }
        return session;
    }

    private static TraceSessionLibrary MakeLib(string path) =>
        new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance);

    private static TraceSessionAutoSaver MakeSaver(
        string autoSavePath,
        ITraceSessionService session,
        IAutoSavePrefsStore prefs,
        IMessageBoxPrompt prompt)
    {
        var library = MakeLib(autoSavePath);
        return new TraceSessionAutoSaver(
            session, library, prefs, prompt,
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
        var src = new TraceSource("src1", "highway", @"C:/r.asc", Colors.Red, new LineStyle());
        var session = MakeSessionWith(src);
        var prefs = new InMemoryPrefsStore();
        var prompt = Substitute.For<IMessageBoxPrompt>();
        var sut = MakeSaver(path, session, prefs, prompt);

        // Act
        var wrote = await sut.TrySaveAutoSnapshotAsync(CancellationToken.None);

        // Assert
        wrote.Should().BeTrue();
        File.Exists(path).Should().BeTrue("the auto-save file must exist after a successful write");
        var loaded = MakeLib(path).Load(path);
        loaded.Should().NotBeNull();
        loaded!.Sources.Should().HaveCount(1);
        loaded.Sources[0].DisplayName.Should().Be("highway");
        // v3.x Task 4: 快照直接取自 service。
        session.Received(1).BuildSnapshot();
    }

    [Fact]
    public async Task TrySaveAutoSnapshotAsync_WithNoSources_ReturnsFalse()
    {
        // Arrange — empty service (no content).
        var path = NewAutoSavePath();
        var session = MakeSessionWith(src: null);
        var prefs = new InMemoryPrefsStore();
        var prompt = Substitute.For<IMessageBoxPrompt>();
        var sut = MakeSaver(path, session, prefs, prompt);

        // Act
        var wrote = await sut.TrySaveAutoSnapshotAsync(CancellationToken.None);

        // Assert
        wrote.Should().BeFalse("an empty session has nothing worth persisting");
        File.Exists(path).Should().BeFalse("we must NOT create a zero-source file");
        session.DidNotReceive().BuildSnapshot();
    }

    [Fact]
    public async Task TryLoadAutoSnapshotAsync_ReturnsNullWhenFileMissing()
    {
        // Arrange — path does not exist on disk.
        var path = NewAutoSavePath();
        var session = MakeSessionWith(src: null);
        var prefs = new InMemoryPrefsStore();
        var prompt = Substitute.For<IMessageBoxPrompt>();
        var sut = MakeSaver(path, session, prefs, prompt);

        // Act
        var result = await sut.TryLoadAutoSnapshotAsync(CancellationToken.None);

        // Assert
        result.Dto.Should().BeNull();
        result.SourceFile.Should().BeEmpty();
        result.Should().Be(AutoLoadResult.None);
    }

    [Fact]
    public async Task TryLoadAutoSnapshotAsync_RoundTripsDtoFromService()
    {
        // Arrange — write a bundle via the saver, then load it back.
        var path = NewAutoSavePath();
        var src = new TraceSource(
            "srcA", "drive_downtown", @"C:/rec.asc",
            new Color(0x12, 0x34, 0x56, 255), new LineStyle());
        var session = MakeSessionWith(src);
        var prefs = new InMemoryPrefsStore();
        var prompt = Substitute.For<IMessageBoxPrompt>();
        var sut = MakeSaver(path, session, prefs, prompt);
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
        session.Received(1).BuildSnapshot();
    }

    [Fact]
    public async Task ApplyAutoSnapshotAsync_UserSaysNo_PersistsNeverRestoreFlag()
    {
        // Arrange — bundle exists; user answers No.
        var path = NewAutoSavePath();
        var src = new TraceSource("src1", "trip", @"C:/t.asc", Colors.Green, new LineStyle());
        var session = MakeSessionWith(src);
        var prefs = new InMemoryPrefsStore();
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
            .Returns(MessageBoxResult.No);
        var sut = MakeSaver(path, session, prefs, prompt);
        // 先落盘一个 bundle，Apply 才能加载它。
        (await sut.TrySaveAutoSnapshotAsync(CancellationToken.None)).Should().BeTrue();

        // Act
        var outcome = await sut.ApplyAutoSnapshotAsync(session, CancellationToken.None);

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
        var src = new TraceSource("src1", "trip", @"C:/t.asc", Colors.Green, new LineStyle());
        var session = MakeSessionWith(src);
        var prefs = new InMemoryPrefsStore { Current = new AutoSavePrefs(NeverRestore: true) };
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
            .Returns(MessageBoxResult.Yes);
        var sut = MakeSaver(path, session, prefs, prompt);
        (await sut.TrySaveAutoSnapshotAsync(CancellationToken.None)).Should().BeTrue();

        // Act
        var outcome = await sut.ApplyAutoSnapshotAsync(session, CancellationToken.None);

        // Assert
        outcome.Applied.Should().BeFalse();
        outcome.PromptShown.Should().BeFalse();
        outcome.Answer.Should().Be(RestoreAnswer.NeverRestore);
        await prompt.DidNotReceiveWithAnyArgs().ShowAsync(default!, default!, default);
    }
}
