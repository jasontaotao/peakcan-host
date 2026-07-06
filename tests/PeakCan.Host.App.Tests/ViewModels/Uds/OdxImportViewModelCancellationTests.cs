using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core.Uds.Odx;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// v3.9.0 MINOR P6: <see cref="OdxImportViewModel.CancelImport"/> must
/// actually cancel the in-flight <c>IOdxImportService.ImportAsync</c>
/// task. v3.8.8 PATCH F3 added the CancelImport method but it only
/// reset UI state; the orphan import task ran to completion. P6
/// threads a <see cref="CancellationToken"/> through the service
/// (deep fix) so CancelImport's call to <c>_importCts.Cancel()</c>
/// actually stops the parse.
/// </summary>
public class OdxImportViewModelCancellationTests
{
    /// <summary>
    /// Stub service whose ImportAsync blocks on a TaskCompletionSource
    /// that the test controls. When the CT is cancelled, the service
    /// throws <see cref="OperationCanceledException"/> (via
    /// <c>ct.ThrowIfCancellationRequested()</c> at the start of its
    /// loop iteration).
    /// </summary>
    private sealed class CancellationAwareOdxImportService : IOdxImportService
    {
        private readonly TaskCompletionSource<OdxImportResult> _tcs = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Number of times the import was entered (0 or 1).</summary>
        public int ImportEnteredCount { get; private set; }

        /// <summary>True if the CT was cancelled before the import returned.</summary>
        public bool CancellationObserved { get; private set; }

        public async Task<OdxImportResult> ImportAsync(
            string odxPath, CancellationToken ct = default)
        {
            ImportEnteredCount++;
            // Register a callback that records the cancellation. This
            // proves the VM's CT was actually threaded through to the
            // service (not just allocated and discarded).
            using var registration = ct.Register(() =>
            {
                CancellationObserved = true;
                _tcs.TrySetCanceled();
            });
            return await _tcs.Task.ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task CancelImport_WhileServiceImportInFlight_PropagatesCancellationToService()
    {
        // ARRANGE: a service whose ImportAsync blocks until the test
        // completes the TCS (or cancellation sets it). The CT
        // registration proves the VM-owned CTS was actually threaded
        // through to the service.
        var svc = new CancellationAwareOdxImportService();
        var vm = new OdxImportViewModel(svc, NullLogger<OdxImportViewModel>.Instance);

        // ACT: kick off the import (no await) and verify it's in flight.
        var importTask = vm.ImportAsync("big.odx");
        await Task.Yield();
        vm.IsBusy.Should().BeTrue("preconditions: the in-flight import must have flipped IsBusy=true");
        svc.ImportEnteredCount.Should().Be(1, "ImportAsync must have entered the service");

        // Cancel from the "window closed" path.
        vm.CancelImport();

        // ASSERT P6: the service's CT was observed (proving the VM
        // threaded the CTS through to the service). Pre-fix (v3.8.8),
        // CancelImport did not touch any service CT; the orphan
        // import task ran to completion even after CancelImport.
        // Allow up to 500ms for the cancellation callback to fire
        // (it's registered via ct.Register which is synchronous).
        await WaitFor(() => svc.CancellationObserved, 500);

        svc.CancellationObserved.Should().BeTrue(
            "v3.9.0 P6: CancelImport must thread the VM-owned CTS to the service so the in-flight import actually stops");

        // The OCE catch arm in ImportAsync absorbs the exception; the
        // CT catch arm does NOT overwrite LastStatus (the "Cancelled"
        // status set by CancelImport is preserved).
        vm.LastStatus.Should().Contain("cancel",
            "v3.9.0 P6: the 'Cancelled' status set by CancelImport must be preserved when OCE propagates from the service");
        vm.IsBusy.Should().BeFalse("postcondition: IsBusy must be false after CancelImport + OCE propagation");
    }

    private static async Task WaitFor(Func<bool> predicate, int millisecondsTimeout)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(millisecondsTimeout);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
    }
}
