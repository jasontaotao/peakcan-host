using System.IO;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class HilStudioViewModel
{
    [RelayCommand]
    private async Task OpenAsync()
    {
        var path = _fileDialog.ShowOpenDialog("DBC files (*.dbc)|*.dbc|All files|*.*");
        if (path is null) return;
        LoadedPath = path;
        Status = "Parsing...";
        await _svc.LoadAsync(path, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>种子加载: 窗口打开时若主窗已加载 DBC 则直接显示（镜像 EcuScriptEditorViewModel.LoadInitialPath）。</summary>
    public void RefreshFromCurrent()
    {
        if (_svc.Current is { } doc)
            Rebuild(doc);
    }

    private void OnLoaded(DbcDocument doc) => ((Action)(() => Rebuild(doc))).RunOnUi();

    private void OnLoadFailed(Error error) => ((Action)(() => Status = $"FAIL: {error.Code} {error.Message}")).RunOnUi();

    private void Rebuild(DbcDocument doc)
    {
        Messages.Clear();
        FilteredMessages.Clear();
        _allMessages.Clear();
        foreach (var m in doc.Messages)
        {
            var row = HilStudioDbcMessageRow.From(m, doc.ValueTables);
            Messages.Add(row);
            _allMessages.Add(row);
        }
        TotalMessages = doc.Messages.Count;
        TotalSignals = _allMessages.Sum(r => r.SignalCount);
        LoadedPath = doc.SourcePath ?? LoadedPath;
        Status = $"Loaded {TotalMessages} messages, {TotalSignals} signals from {Path.GetFileName(LoadedPath)}";
        SelectedMessage = null;
        SelectedSignal = null;
        ApplyFilter();
    }
}
