using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.ViewModels.Uds.Rows;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.Database;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// Panel VM for the DIDs tab: DataGrid backed by DidDatabase.All,
/// Read/Write commands for the selected row.
/// </summary>
public sealed partial class DidPanelViewModel : ObservableObject, IUdsPanel
{
    private readonly UdsClient _udsClient;
    private readonly DidDatabase _didDb;
    private ObservableCollection<UdsLogLine>? _log;

    public ObservableCollection<DidRow> Dids { get; } = new();

    [ObservableProperty] private DidRow? _selectedDid;
    [ObservableProperty] private string  _writeValue = "";
    [ObservableProperty] private string? _lastResult;

    public DidPanelViewModel(UdsClient udsClient, DidDatabase didDb)
    {
        ArgumentNullException.ThrowIfNull(udsClient);
        ArgumentNullException.ThrowIfNull(didDb);
        _udsClient = udsClient;
        // v2.0.6 PATCH Bug-1: store the DidDatabase reference so
        // RefreshFromDatabase can re-populate the panel after an ODX
        // import. Previously the ctor copied DidDatabase.All into Dids
        // and discarded the database reference, so ODX import calls to
        // AddRange mutated the database in-place but the UI never
        // noticed — the panel stayed frozen on the ctor-time snapshot.
        _didDb = didDb;

        foreach (var d in didDb.All)
            Dids.Add(new DidRow
            {
                Id          = d.Id,
                Name        = d.Name,
                LengthBytes = d.LengthBytes,
                Writable    = d.Writable,
            });
        if (Dids.Count > 0) SelectedDid = Dids[0];
    }

    /// <summary>
    /// v2.0.6 PATCH Bug-1: re-populate the DIDs DataGrid from
    /// <see cref="DidDatabase.All"/> after an ODX import (or any other
    /// out-of-band mutation of the database). Mirrors
    /// <c>DtcPanelViewModel.RefreshFromDatabase</c> so all three
    /// database-backed panels stay consistent.
    /// </summary>
    public void RefreshFromDatabase()
    {
        Dids.Clear();
        foreach (var d in _didDb.All)
        {
            Dids.Add(new DidRow
            {
                Id          = d.Id,
                Name        = d.Name,
                LengthBytes = d.LengthBytes,
                Writable    = d.Writable,
            });
        }
        if (Dids.Count > 0) SelectedDid = Dids[0];
    }

    public void AttachLog(ObservableCollection<UdsLogLine> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    [RelayCommand(CanExecute = nameof(CanReadDid))]
    private async Task ReadDidAsync()
    {
        var row = SelectedDid;
        if (row is null) return;
        row.IsReading = true;
        ReadDidCommand.NotifyCanExecuteChanged();
        try
        {
            AppendLog("Info", $"Reading DID 0x{row.Id:X4}...");
            // v2.0.6 PATCH Bug-3: NO ConfigureAwait(false) here — the
            // continuation, catch handlers, and finally block all mutate
            // WPF-bound state (row.ReadValue, row.IsReading, ObservableCollection
            // log entries, RelayCommand.NotifyCanExecuteChanged). With
            // ConfigureAwait(false) those run on the threadpool and cause
            // cross-thread binding access / NotSupportedException / deadlock
            // → "program hangs and crashes" when the SDK times out (e.g.
            // peakcan connected but no ECU on the bus). Removing the flag
            // lets WPF's SynchronizationContext keep the continuation on the
            // UI dispatcher thread. UdsClient methods themselves use
            // ConfigureAwait(false) internally (correct for Core-layer code
            // with no UI dependency); the re-capture happens at this await.
            var data = await _udsClient.ReadDataByIdentifierAsync(row.Id);
            row.ReadValue = BitConverter.ToString(data).Replace("-", " ");
            LastResult    = row.ReadValue;
            AppendLog("Info", $"DID 0x{row.Id:X4} = {row.ReadValue}");
        }
        catch (UdsNegativeResponseException ex)
        {
            AppendLog("Warn", $"Read DID 0x{row.Id:X4} failed: NRC 0x{(byte)ex.ResponseCode:X2}");
        }
        catch (FormatException ex)
        {
            AppendLog("Error", $"Read DID 0x{row.Id:X4}: invalid format — {ex.Message}");
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Read DID 0x{row.Id:X4} error: {ex.Message}");
        }
        finally
        {
            row.IsReading = false;
            ReadDidCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanReadDid() => SelectedDid is { IsReading: false };

    [RelayCommand]
    private async Task WriteDidAsync()
    {
        var row = SelectedDid;
        if (row is null) return;
        try
        {
            var data = ParseHexString(WriteValue);
            AppendLog("Info", $"Writing DID 0x{row.Id:X4} ({data.Length} bytes)...");
            // v2.0.6 PATCH Bug-3: same reasoning as ReadDidAsync — no
            // ConfigureAwait(false) so the surrounding try/catch runs on the
            // UI dispatcher when AppendLog writes to the ObservableCollection.
            await _udsClient.WriteDataByIdentifierAsync(row.Id, data);
            AppendLog("Info", $"DID 0x{row.Id:X4} written successfully");
        }
        catch (UdsNegativeResponseException ex)
        {
            AppendLog("Warn", $"Write DID 0x{row.Id:X4} failed: NRC 0x{(byte)ex.ResponseCode:X2}");
        }
        catch (FormatException ex)
        {
            AppendLog("Error", $"Write DID 0x{row.Id:X4}: invalid hex input — {ex.Message}");
        }
        catch (Exception ex)
        {
            AppendLog("Error", $"Write DID 0x{row.Id:X4} error: {ex.Message}");
        }
    }

    private static byte[] ParseHexString(string hex)
    {
        hex = (hex ?? "").Replace(" ", "").Replace("-", "");
        if (hex.Length % 2 != 0) hex = "0" + hex;
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private void AppendLog(string level, string message)
        => _log?.Add(new UdsLogLine($"{DateTime.Now:HH:mm:ss.fff}", level, message));

    partial void OnSelectedDidChanged(DidRow? value) => ReadDidCommand.NotifyCanExecuteChanged();
}
