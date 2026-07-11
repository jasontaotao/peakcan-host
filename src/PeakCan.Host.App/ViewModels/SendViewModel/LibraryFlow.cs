using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Windows;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class SendViewModel
{
    // Flow C: Library + multi-frame opener (v1.2.11 PATCH Item 5 + v2.1.0 MINOR).
    // Methods moved verbatim from SendViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - All library commands -> _libraryService (DI, main)
    //   - SaveCurrentToLibrary/DeleteFromLibrary -> RefreshLibrary (intra-flow)
    //   - SaveCurrentToLibrary/DeleteFromLibrary -> LogSaveToLibraryFailed/LogDeleteFromLibraryFailed (intra-flow)
    //
    // [RelayCommand] attributes MUST travel with their methods.

    // v1.2.11 PATCH Item 5 UI: library commands bound to the SendView Expander.

    [RelayCommand]
    private void RefreshLibrary()
    {
        Library.Clear();
        if (_libraryService is null) return;
        foreach (var f in _libraryService.Load()) Library.Add(f);
    }

    [RelayCommand]
    private void SaveCurrentToLibrary(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Status = "Frame name is required";
            return;
        }
        if (!uint.TryParse(IdText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
        {
            Status = $"Invalid ID: {IdText}";
            return;
        }
        if (_libraryService is null)
        {
            Status = "Library unavailable";
            return;
        }
        byte[] bytes;
        try { bytes = ParseHex(DataText); }
        catch (FormatException ex)
        {
            Status = $"Invalid data: {ex.Message}";
            return;
        }
        var saved = new SendFrameLibrary.SavedFrame(
            name, raw, IsExtended, IsFd, IsRtr, IsBitRateSwitch,
            Convert.ToHexString(bytes), DateTimeOffset.UtcNow);
        // v1.2.12 PATCH Item 1: route through the atomic Add so concurrent
        // Save calls (e.g. double-clicked button) don't drop each other's
        // read-modify-write. Catch IO / JSON exceptions and surface as a
        // FAIL status rather than letting them escape the WPF dispatcher.
        try
        {
            _libraryService.Add(saved);
            RefreshLibrary();
            Status = $"Saved '{name}' to library ({_libraryService.Count} frames).";
        }
        catch (Exception ex)
        {
            Status = $"FAIL: Save '{name}' to library: {ex.Message}";
            LogSaveToLibraryFailed(_logger, ex, name);
        }
    }

    [RelayCommand]
    private void LoadFromLibrary(SendFrameLibrary.SavedFrame? frame)
    {
        if (frame is null) return;
        IdText = frame.RawId.ToString("X", CultureInfo.InvariantCulture);
        IsExtended = frame.IsExtended;
        IsFd = frame.IsFd;
        IsRtr = frame.IsRtr;
        IsBitRateSwitch = frame.BitRateSwitch;
        DataText = frame.DataHex;
        Status = $"Loaded '{frame.Name}'";
    }

    [RelayCommand]
    private void DeleteFromLibrary(SendFrameLibrary.SavedFrame? frame)
    {
        if (frame is null || _libraryService is null) return;
        // v1.2.12 PATCH Item 1: route through the atomic Remove so concurrent
        // Delete calls don't drop each other's read-modify-write. Report
        // a friendly status when the frame was already gone (idempotent
        // delete), and surface IO failures as FAIL.
        try
        {
            if (_libraryService.Remove(frame.Name))
            {
                RefreshLibrary();
                Status = $"Removed '{frame.Name}' from library.";
            }
            else
            {
                Status = $"'{frame.Name}' not found in library (already removed?).";
            }
        }
        catch (Exception ex)
        {
            Status = $"FAIL: Remove '{frame.Name}': {ex.Message}";
            LogDeleteFromLibraryFailed(_logger, ex, frame.Name);
        }
    }

    // v1.2.12 PATCH Item 1: log IO / JSON failures from the atomic library
    // Add/Remove path. The Status string is user-facing; these messages
    // are the operator-facing diagnostics that survive a UI crash.
    [LoggerMessage(EventId = 2001, Level = LogLevel.Error, Message = "Save '{Name}' to library failed")]
    private static partial void LogSaveToLibraryFailed(ILogger logger, Exception ex, string name);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error, Message = "Delete '{Name}' from library failed")]
    private static partial void LogDeleteFromLibraryFailed(ILogger logger, Exception ex, string name);

    // v2.1.0 MINOR: open the multi-frame send window (non-modal).
    // The window is lazy-created on first call: WPF Window construction
    // requires an STA thread + a live Application, so we can't
    // resolve a Window from DI at container-build time. The VM is
    // DI-resolved (singleton) but the Window itself is owned by
    // SendViewModel and kept alive for the SendView's lifetime.
    private MultiFrameSendWindow? _openMultiFrameWindow;

    [RelayCommand]
    private void OpenMultiFrameSend()
    {
        if (_multiFrameVm is null)
        {
            Status = "Multi-frame window unavailable";
            return;
        }
        if (_openMultiFrameWindow is { } existing && existing.IsVisible)
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        _openMultiFrameWindow = new MultiFrameSendWindow(_multiFrameVm);
        if (Application.Current?.MainWindow is { } owner && owner != _openMultiFrameWindow)
            _openMultiFrameWindow.Owner = owner;
        // v3.9.2 PATCH L3: mirror the v3.9.1 PATCH B1 fix
        // (AppShellViewModel._traceViewerView.Closed reset) so the next
        // OpenMultiFrameSend click takes the fresh-window path instead
        // of stomp-and-leak on a closed instance.
        _openMultiFrameWindow.Closed += (_, _) => _openMultiFrameWindow = null;
        _openMultiFrameWindow.Show();
        Status = "Multi-frame send window opened";
    }
}