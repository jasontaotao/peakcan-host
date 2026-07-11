using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Models;
using PeakCan.Host.App.Services.Sequence;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class MultiFrameSendViewModel
{
    // Flow C: Library (v2.1.2 PATCH + earlier).
    // Methods + 2 helpers moved verbatim from MultiFrameSendViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - All 4 methods -> _library (DI, main) + SavedSequences (state, main)
    //   - SaveCurrent -> ReplaceOrAddInPicker (intra-flow)
    //   - SaveCurrent -> BuildSavedSequence (intra-flow helper)
    //   - LoadSaved -> Rows.Clear + MaterializeRow (intra-flow helper)
    //   - SnapshotRow/MaterializeRow -> MultiFrameSequenceRow (state, main)
    //
    // [RelayCommand] attributes MUST travel with their methods.

    // ===== v2.1.2 PATCH: Save / Load / Delete sequence =====

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void SaveCurrent()
    {
        if (_library is null) { StatusText = "Sequence library unavailable"; return; }
        var name = (SaveNameText ?? "").Trim();
        if (string.IsNullOrEmpty(name)) { StatusText = "Sequence name is required"; return; }
        var saved = BuildSavedSequence(name);
        try
        {
            var count = _library.Add(saved);
            // Refresh the picker — last-wins on duplicate name, so
            // always update the in-memory list.
            ReplaceOrAddInPicker(saved);
            StatusText = $"Saved '{name}' ({count} sequence(s) in library).";
        }
        catch (Exception ex)
        {
            StatusText = $"FAIL: Save '{name}': {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void LoadSaved(SequenceLibrary.SavedSequence? saved)
    {
        if (saved is null || _library is null) return;
        try
        {
            IsConcurrent = saved.Mode == SequenceLibrary.Mode.Concurrent;
            DelayMs = saved.DelayMs;
            Iterations = saved.Iterations;
            Rows.Clear();
            foreach (var sr in saved.Rows)
            {
                var row = MaterializeRow(sr);
                Rows.Add(row);
            }
            SelectedRow = Rows.Count > 0 ? Rows[0] : null;
            StatusText = $"Loaded '{saved.Name}' ({saved.Rows.Count} row(s)).";
        }
        catch (Exception ex)
        {
            StatusText = $"FAIL: Load '{saved?.Name}': {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void DeleteSaved(SequenceLibrary.SavedSequence? saved)
    {
        if (saved is null || _library is null) return;
        try
        {
            if (_library.Remove(saved.Name))
            {
                SavedSequences.Remove(saved);
                StatusText = $"Removed '{saved.Name}'.";
            }
            else
            {
                StatusText = $"'{saved.Name}' not found in library.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"FAIL: Delete '{saved.Name}': {ex.Message}";
        }
    }

    private void ReplaceOrAddInPicker(SequenceLibrary.SavedSequence saved)
    {
        for (var i = 0; i < SavedSequences.Count; i++)
        {
            if (SavedSequences[i].Name == saved.Name)
            {
                SavedSequences[i] = saved;
                return;
            }
        }
        SavedSequences.Add(saved);
    }

    /// <summary>Snapshot the current Rows + mode into a SavedSequence record.</summary>
    private SequenceLibrary.SavedSequence BuildSavedSequence(string name) =>
        new(
            Name: name,
            Mode: IsConcurrent ? SequenceLibrary.Mode.Concurrent : SequenceLibrary.Mode.Sequential,
            DelayMs: DelayMs,
            Iterations: Iterations,
            Rows: Rows.Select(SnapshotRow).ToList(),
            SavedAt: DateTimeOffset.UtcNow);

    private static SequenceLibrary.SavedRow SnapshotRow(MultiFrameSequenceRow row) =>
        new()
        {
            Kind = row.RowKind == MultiFrameSequenceRow.Kind.Dbc
                ? SequenceLibrary.RowKind.Dbc
                : SequenceLibrary.RowKind.Raw,
            Id = row.Id,
            DataHex = row.DataHex,
            IsExtended = row.IsExtended,
            IsFd = row.IsFd,
            IsRtr = row.IsRtr,
            IsBitRateSwitch = row.IsBitRateSwitch,
            IsErrorStateIndicator = row.IsErrorStateIndicator,
            DbcMessageName = row.DbcMessageName,
            DbcSignalValues = row.DbcSignalValues.Select(s => new SequenceLibrary.SavedSignalValue
            {
                Name = s.Name,
                Value = s.Value,
            }).ToList(),
        };

    private static MultiFrameSequenceRow MaterializeRow(SequenceLibrary.SavedRow sr)
    {
        var row = new MultiFrameSequenceRow
        {
            RowKind = sr.Kind == SequenceLibrary.RowKind.Dbc
                ? MultiFrameSequenceRow.Kind.Dbc
                : MultiFrameSequenceRow.Kind.Raw,
            Id = sr.Id,
            DataHex = sr.DataHex,
            IsExtended = sr.IsExtended,
            IsFd = sr.IsFd,
            IsRtr = sr.IsRtr,
            IsBitRateSwitch = sr.IsBitRateSwitch,
            IsErrorStateIndicator = sr.IsErrorStateIndicator,
            DbcMessageName = sr.DbcMessageName,
        };
        foreach (var sv in sr.DbcSignalValues)
        {
            row.DbcSignalValues.Add(new DbcSignalValue { Name = sv.Name, Value = sv.Value });
        }
        return row;
    }
}