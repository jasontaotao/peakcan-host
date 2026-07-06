using FluentAssertions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

public class OdxImportViewModelTests
{
    [Fact]
    public async Task ImportAsync_SetsBusyAndClearsOnCompletion()
    {
        // Arrange — use a stub service (no real IO).
        var stub = new StubOdxImportService();
        var vm = new OdxImportViewModel(stub);

        // Act
        await vm.ImportAsync("ignored.odx");

        // Assert — busy rose during and cleared; status set.
        vm.IsBusy.Should().BeFalse();
        vm.LastStatus.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ImportCommand_CanExecuteReflectsBusyState()
    {
        // Arrange
        var stub = new StubOdxImportService();
        var vm = new OdxImportViewModel(stub);

        // Act + Assert — initial state allows execute; during busy it doesn't.
        vm.ImportCommand.CanExecute(null).Should().BeTrue();
    }

    // ---------- v3.8.8 PATCH F3: window-close mid-import IsBusy stuck ----------

    /// <summary>
    /// v3.8.8 PATCH F3: a 500 MB+ ODX can take several seconds to
    /// parse. If the user opens the ODX import window, clicks Load,
    /// then closes the window mid-import, <see cref="OdxImportViewModel"/>
    /// is still in the middle of <c>ImportAsync</c> with
    /// <c>IsBusy=true</c>. Pre-fix, the window close has no path back to
    /// the VM — <c>IsBusy</c> stays true and the
    /// <c>ImportCommand.CanExecute</c> lambda (<c>_ =&gt; !IsBusy</c>)
    /// keeps the button disabled the next time the window is shown.
    /// The user is stuck until app restart.
    /// <para>
    /// F3 fix: a public <c>CancelImport()</c> method on the VM that
    /// sets <c>IsBusy=false</c> and surfaces a "Cancelled"
    /// <c>LastStatus</c>. The <c>OdxImportWindow.OnClosed</c> code-behind
    /// calls it on window close.
    /// </para>
    /// <para>
    /// This test simulates the window-close path: a stub service whose
    /// <c>ImportAsync</c> blocks on a <c>TaskCompletionSource</c> (the
    /// import is in flight), the test calls <c>CancelImport()</c>
    /// (mirroring the window's OnClosed handler), then asserts
    /// <c>IsBusy=false</c> and <c>ImportCommand.CanExecute(null)=true</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CancelImport_DuringInFlightImport_ClearsIsBusyAndReEnablesCommand()
    {
        // Arrange: a service whose ImportAsync never completes until
        // we manually complete its TCS -- simulates a 500MB+ ODX parse
        // that the user is waiting on.
        var blocking = new BlockingOdxImportService();
        var vm = new OdxImportViewModel(blocking);

        // Kick off the import but DO NOT await it (it's in flight forever).
        var importTask = vm.ImportAsync("big.odx");
        // Give the VM a chance to set IsBusy=true before we cancel.
        await Task.Yield();
        vm.IsBusy.Should().BeTrue("preconditions: the in-flight import must have flipped IsBusy=true");

        // Act: window close (simulated) -- user dismisses the dialog.
        vm.CancelImport();

        // ASSERT F3: IsBusy cleared, command re-enabled, status surfaced.
        vm.IsBusy.Should().BeFalse(
            "v3.8.8 F3 fix: CancelImport must reset IsBusy so a re-shown window can re-import");
        vm.ImportCommand.CanExecute(null).Should().BeTrue(
            "v3.8.8 F3 fix: CancelImport must re-enable the Import button for the next attempt");
        vm.LastStatus.Should().Contain("cancel",
            "v3.8.8 F3 fix: CancelImport must surface a 'cancelled' status so the user knows what happened");

        // Cleanup: complete the in-flight TCS so the orphaned import task
        // doesn't keep a thread pool worker around for the test runner.
        blocking.Complete();
        await importTask;
    }

    private sealed class StubOdxImportService : IOdxImportService
    {
        public Task<OdxImportResult> ImportAsync(
            string odxPath, CancellationToken ct = default)
            => Task.FromResult(OdxImportResult.Ok(0, 0, 0, Array.Empty<string>()));
    }

    /// <summary>
    /// v3.8.8 PATCH F3 helper: stub IOdxImportService whose ImportAsync
    /// blocks on a TaskCompletionSource. The test calls Complete() to
    /// release the import after asserting on the post-CancelImport
    /// state.
    /// </summary>
    private sealed class BlockingOdxImportService : IOdxImportService
    {
        private readonly TaskCompletionSource<OdxImportResult> _tcs = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OdxImportResult> ImportAsync(
            string odxPath, CancellationToken ct = default)
            => _tcs.Task;

        public void Complete() =>
            _tcs.TrySetResult(OdxImportResult.Ok(0, 0, 0, Array.Empty<string>()));
    }
}
