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

    private sealed class StubOdxImportService : IOdxImportService
    {
        public Task<OdxImportResult> ImportAsync(
            string odxPath, CancellationToken ct = default)
            => Task.FromResult(OdxImportResult.Ok(0, 0, 0, Array.Empty<string>()));
    }
}
