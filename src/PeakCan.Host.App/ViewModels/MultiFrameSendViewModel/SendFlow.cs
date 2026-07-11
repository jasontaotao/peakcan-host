using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.MultiFrame;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class MultiFrameSendViewModel
{
    // Flow B: Send (v2.1.1 PATCH + earlier).
    // Methods + Can* predicates + ModeLabel helper moved verbatim
    // from MultiFrameSendViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - SendAsync -> _service (DI, main) + Rows (state, main) + _runCts (intra-flow state, main)
    //
    // [RelayCommand] attributes MUST travel with their methods.

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (Rows.Count == 0)
        {
            StatusText = "No frames to send.";
            return;
        }
        // v2.1.1 PATCH: pass rows directly to the service; the
        // service handles row.Build() (raw) or DbcEncodeService
        // (DBC) per-row. Per-row build failures (bad hex, missing
        // DBC message) count as failures but don't abort the
        // sequence.

        IsRunning = true;
        StatusText = $"Sending {Rows.Count} frame(s) × {Iterations} iteration(s) ({ModeLabel()})…";
        ProgressValue = 0;
        RefreshProgressMax();

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        var mode = IsConcurrent ? SequenceSendService.Mode.Concurrent : SequenceSendService.Mode.Sequential;

        // IProgress<int> marshals to the UI thread via the captured
        // SynchronizationContext (we're already on the UI dispatcher
        // when SendAsync runs).
        var progress = new Progress<int>(v => ProgressValue = v);

        try
        {
            var result = await _service.SendAsync(Rows, mode, DelayMs, Iterations, progress, ct).ConfigureAwait(true);
            StatusText = result.FailureCount == 0
                ? $"Done. Sent {result.SentCount} / {result.SentCount + result.FailureCount} in {result.IterationsCompleted} iteration(s)."
                : $"Done with errors. Sent {result.SentCount}, failed {result.FailureCount}, iterations {result.IterationsCompleted}.";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Cancelled at {ProgressValue}/{ProgressMax}.";
        }
        catch (Exception ex)
        {
            StatusText = $"FAIL: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private bool CanSend() => !IsRunning && Rows.Count > 0;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _runCts?.Cancel();
    }

    private bool CanStop() => IsRunning;

    private string ModeLabel() => IsConcurrent ? "concurrent" : $"sequential @ {DelayMs}ms";
}