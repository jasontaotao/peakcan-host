using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.Scripting;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class ScriptViewModel
{
    // Flow: Output buffering + flush (script engine output → UI ObservableCollection).
    // Methods + fields moved verbatim from ScriptViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - Ctor -> _outputBuffer + _bufferLock initializers (moved here)
    //   - Ctor -> _flushTimer.Tick += (_, _) => FlushOutputBuffer (intra-flow)
    //   - OnOutputReceived <- _engine.OutputReceived (ctor subscription, main)
    //   - FlushOutputBuffer -> OutputLines.Add / .RemoveAt (main [ObservableProperty] collection)
    //   - ClearOutput -> OutputLines.Clear + _outputBuffer.Clear (intra-flow)

    // Buffer for output lines from the script engine.
    private readonly Queue<ScriptOutputLine> _outputBuffer = new();
    private readonly object _bufferLock = new();

    /// <summary>Maximum output lines to keep in the UI.</summary>
    public const int MaxOutputLines = 1000;

    /// <summary>Clear the output panel.</summary>
    [RelayCommand]
    private void ClearOutput()
    {
        OutputLines.Clear();
        lock (_bufferLock)
        {
            _outputBuffer.Clear();
        }
    }

    /// <summary>
    /// Called by the script engine when output is produced.
    /// Queues the line for UI flush.
    /// </summary>
    private void OnOutputReceived(ScriptOutputLine line)
    {
        lock (_bufferLock)
        {
            _outputBuffer.Enqueue(line);
        }
    }

    /// <summary>
    /// Flush buffered output lines to the UI collection.
    /// Called at 30 Hz by the dispatcher timer.
    /// </summary>
    private void FlushOutputBuffer()
    {
        ScriptOutputLine[] lines;
        lock (_bufferLock)
        {
            if (_outputBuffer.Count == 0) return;
            lines = [.. _outputBuffer];
            _outputBuffer.Clear();
        }

        foreach (var line in lines)
        {
            var prefix = line.Level switch
            {
                ScriptOutputLevel.Warning => "⚠ ",
                ScriptOutputLevel.Error => "❌ ",
                _ => ""
            };

            var formatted = $"[{line.Timestamp:HH:mm:ss}] {prefix}{line.Message}";
            OutputLines.Add(formatted);

            // Trim old lines if we exceed the limit.
            while (OutputLines.Count > MaxOutputLines)
            {
                OutputLines.RemoveAt(0);
            }
        }
    }
}