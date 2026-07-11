using System.Collections.Specialized;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Models;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class MultiFrameSendViewModel
{
    // Flow A: RowManagement (v2.1.0 MINOR + v2.1.1 PATCH + earlier).
    // Methods + 2 helpers moved verbatim from MultiFrameSendViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - All 6 row commands -> Rows (state, main) + SelectedRow (state, main)
    //   - OnIterationsChanged -> RefreshProgressMax (intra-flow)
    //
    // [RelayCommand] attributes MUST travel with their methods.

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RefreshProgressMax();

    private void RefreshProgressMax()
    {
        ProgressMax = Math.Max(0, Rows.Count) * Math.Max(1, Iterations);
    }

    partial void OnIterationsChanged(int value) => RefreshProgressMax();

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void AddRow()
    {
        Rows.Add(new MultiFrameSequenceRow());
        SelectedRow = Rows[^1];
    }

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void RemoveRow()
    {
        if (SelectedRow is null) return;
        var idx = Rows.IndexOf(SelectedRow);
        Rows.Remove(SelectedRow);
        if (Rows.Count > 0)
            SelectedRow = Rows[Math.Min(idx, Rows.Count - 1)];
        else
            SelectedRow = null;
    }

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void DuplicateRow()
    {
        if (SelectedRow is null) return;
        var idx = Rows.IndexOf(SelectedRow);
        var clone = new MultiFrameSequenceRow
        {
            Id = SelectedRow.Id,
            DataHex = SelectedRow.DataHex,
            IsExtended = SelectedRow.IsExtended,
            IsFd = SelectedRow.IsFd,
            IsRtr = SelectedRow.IsRtr,
            IsBitRateSwitch = SelectedRow.IsBitRateSwitch,
            IsErrorStateIndicator = SelectedRow.IsErrorStateIndicator,
        };
        Rows.Insert(idx + 1, clone);
        SelectedRow = clone;
    }

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void MoveUp()
    {
        if (SelectedRow is null) return;
        var idx = Rows.IndexOf(SelectedRow);
        if (idx <= 0) return;
        Rows.Move(idx, idx - 1);
    }

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void MoveDown()
    {
        if (SelectedRow is null) return;
        var idx = Rows.IndexOf(SelectedRow);
        if (idx < 0 || idx >= Rows.Count - 1) return;
        Rows.Move(idx, idx + 1);
    }

    [RelayCommand(CanExecute = nameof(CanEditRows))]
    private void ClearRows()
    {
        Rows.Clear();
        SelectedRow = null;
    }
}