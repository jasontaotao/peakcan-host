using System.IO;
using System.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services.Trace;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// v3.10.0 MINOR T2 (C3): pins the three contract guarantees of the
/// new <see cref="SessionAutoSaver{TVm}"/> generic base class.
/// </summary>
public sealed class SessionAutoSaverBaseTests
{
    private sealed record FakeVm(string Name);

    private sealed class FakeAutoSaver : SessionAutoSaver<FakeVm>
    {
        public bool BuildSnapshotCalled { get; private set; }
        private readonly FakeVm? _vmToReturn;
        private readonly bool _hasContent;

        public FakeAutoSaver(
            TraceSessionLibrary library,
            IAutoSavePrefsStore prefs,
            IMessageBoxPrompt prompt,
            string autoSavePath,
            FakeVm? vmToReturn,
            bool hasContent = true)
            : base(library, prefs, prompt,
                   NullLogger<FakeAutoSaver>.Instance, autoSavePath)
        {
            _vmToReturn = vmToReturn;
            _hasContent = hasContent;
        }

        protected override FakeVm? GetActiveVm() => _vmToReturn;
        protected override bool HasContentToSave(FakeVm vm) => _hasContent;
        protected override string RestorePromptTitle => "Restore fake session?";
        protected override TraceSessionBundleDto BuildSnapshot(FakeVm vm)
        {
            BuildSnapshotCalled = true;
            var dto = new TraceSessionBundleDto
            {
                AppVersion = "test"
            };
            dto.Sources.Add(new BundleSourceDto
            {
                SourceId = "src1",
                DisplayName = vm.Name,
                Path = @"C:/fake.asc"
            });
            return dto;
        }
        protected override Task<IReadOnlyList<string>> ApplySnapshotToVmAsync(
            FakeVm vm, string sourceFile)
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    [Fact]
    public async Task TrySave_NoActiveVm_ReturnsFalse()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"fake-save-{Guid.NewGuid():N}.tmtrace");
        var saver = new FakeAutoSaver(
            new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance),
            new InMemoryPrefsStore(),
            Substitute.For<IMessageBoxPrompt>(),
            path, vmToReturn: null);

        var wrote = await saver.TrySaveAutoSnapshotAsync(CancellationToken.None);

        wrote.Should().BeFalse();
        saver.BuildSnapshotCalled.Should().BeFalse();
    }

    [Fact]
    public async Task TryLoad_MissingFile_ReturnsNone()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"fake-load-{Guid.NewGuid():N}.tmtrace");
        var saver = new FakeAutoSaver(
            new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance),
            new InMemoryPrefsStore(),
            Substitute.For<IMessageBoxPrompt>(),
            path, vmToReturn: new FakeVm("anything"));

        var result = await saver.TryLoadAutoSnapshotAsync(CancellationToken.None);

        result.Should().Be(AutoLoadResult.None);
        result.Dto.Should().BeNull();
    }

    [Fact]
    public async Task Apply_NeverRestoreTrue_SkipsPrompt()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"fake-apply-{Guid.NewGuid():N}.tmtrace");
        var prefs = new InMemoryPrefsStore
        {
            Current = new AutoSavePrefs(NeverRestore: true)
        };
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window>())
              .Returns(MessageBoxResult.Yes);
        var library = new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance);
        var vm = new FakeVm("trip");
        var saver = new FakeAutoSaver(library, prefs, prompt, path, vm);
        (await saver.TrySaveAutoSnapshotAsync(CancellationToken.None)).Should().BeTrue();

        var outcome = await saver.ApplyAutoSnapshotAsync(vm, CancellationToken.None);

        outcome.Applied.Should().BeFalse();
        outcome.PromptShown.Should().BeFalse();
        outcome.Answer.Should().Be(RestoreAnswer.NeverRestore);
        await prompt.DidNotReceiveWithAnyArgs().ShowAsync(default!, default!, default);
    }
}
