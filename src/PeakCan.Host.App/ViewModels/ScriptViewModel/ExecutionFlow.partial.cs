using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class ScriptViewModel
{
    // Flow: Script execution (Run + Stop commands).
    // Methods moved verbatim from ScriptViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - RunAsync -> _engine.RunAsync (main field)
    //   - RunAsync -> LogScriptCompleted / LogScriptFailed / LogScriptException (Flow: Logging)
    //   - Stop -> _engine.Stop (main field)
    //   - Stop -> LogScriptStopped (Flow: Logging)
    //   - CanRun / CanStop -> IsRunning (main [ObservableProperty] property)
    //
    // [RelayCommand] attributes MUST travel with their methods.

    /// <summary>Run the current script.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(ScriptText)) return;

        IsRunning = true;
        StatusText = "Running...";
        OutputLines.Clear();

        try
        {
            var result = await _engine.RunAsync(ScriptText).ConfigureAwait(true);

            if (result.Success)
            {
                StatusText = "Completed";
                LogScriptCompleted(_logger);
            }
            else
            {
                StatusText = $"Error: {result.Error}";
                LogScriptFailed(_logger, result.Error ?? "Unknown error");
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            LogScriptException(_logger, ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private bool CanRun() => !IsRunning;

    /// <summary>Stop the running script.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _engine.Stop();
        StatusText = "Stopped";
        IsRunning = false;
        LogScriptStopped(_logger);
    }

    private bool CanStop() => IsRunning;
}
