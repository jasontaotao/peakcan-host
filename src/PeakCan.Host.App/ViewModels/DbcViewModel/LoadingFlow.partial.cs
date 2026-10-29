using System.IO;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class DbcViewModel
{
    // Flow: DBC loading (OpenAsync + event handlers).
    // Methods moved verbatim from DbcViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - Ctor -> _svc.DbcLoaded += OnLoaded; _svc.LoadFailed += OnLoadFailed (main ctor subscriptions)
    //   - OpenAsync -> _fileDialog.ShowOpenDialog (main field) + LogOpenInvoked (main [LoggerMessage])
    //   - OnLoaded -> Messages.Clear + FilteredMessages.Clear + _allMessages (main fields) + ApplyFilter (Flow B)
    //   - OnLoaded -> _signals.Reset + _signals.SetDbcService (main field)
    //   - OnLoaded -> TotalMessages + TotalSignals + Status (main [ObservableProperty])
    //   - OnLoadFailed -> Status (main [ObservableProperty])
    //
    // [RelayCommand] attribute MUST travel with OpenAsync method.

    [RelayCommand]
    private async Task OpenAsync()
    {
        var path = _fileDialog.ShowOpenDialog("DBC files (*.dbc)|*.dbc|All files|*.*");
        if (path is null) return;
        await LoadPathAsync(path).ConfigureAwait(true);
    }

    /// <summary>
    /// 界面发起的加载（「打开 DBC…」与「重试」共用）。记下路径供
    /// 「重试」「打开目录」定位——脚本侧的加载不走这里，所以不会覆盖成别处的路径。
    /// </summary>
    private async Task LoadPathAsync(string path)
    {
        // 计数必须在 try 内自增：中间任何一句抛异常都不能让计数泄漏，
        // 否则之后所有"外来失败"都会被误认为界面发起，MEDIUM-2 的错位重试会复发。
        try
        {
            _uiLoadInFlight++;
            _lastOpenPath = path;
            NotifyRecoveryCommands();
            LoadedPath = path;
            Status = "解析中…";
            LogOpenInvoked(_logger, path);
            await _svc.LoadAsync(path).ConfigureAwait(true);
        }
        finally
        {
            // 计数（不是 bool）：两次界面加载重叠时，先结束的那次不能把仍在飞的
            // 另一次"注销"掉，否则后者的失败会被当成外来失败。
            _uiLoadInFlight--;
        }
    }

    /// <summary>刷新「重试 / 打开目录」的可用态。路径一变就得通知，否则按钮停在旧状态。</summary>
    private void NotifyRecoveryCommands()
    {
        RetryLoadCommand.NotifyCanExecuteChanged();
        RevealDbcFileCommand.NotifyCanExecuteChanged();
    }

    /// <summary>P1 防事故：用同一个路径重试最近一次失败的加载。</summary>
    [RelayCommand(CanExecute = nameof(CanRetryLoad))]
    private async Task RetryLoadAsync()
    {
        if (_lastOpenPath is not { } path) return;
        await LoadPathAsync(path).ConfigureAwait(true);
    }

    private bool CanRetryLoad() => _lastOpenPath is not null;

    /// <summary>P1 防事故：在资源管理器里定位那个加载失败的 DBC，方便就地修。</summary>
    [RelayCommand(CanExecute = nameof(CanRevealDbcFile))]
    private void RevealDbcFile()
    {
        if (_lastOpenPath is not { } path) return;
        try
        {
            _shell.Reveal(path);
        }
        catch (Exception ex)
        {
            // 不静默：explorer 起不来 / 文件被删了，都要让用户看到。
            Status = $"无法打开所在目录：{ex.Message}";
        }
    }

    private bool CanRevealDbcFile() => _lastOpenPath is not null;

    /// <summary>
    /// P1 防事故：打开日志。有 <c>peak-*.log</c> 就定位到最近写入的那一份
    /// （按写时间取最新，不判日期——跨零点但新文件还没落盘时会停在昨天那份，可接受），
    /// 否则退到目录（目录也没有就明说，不装作打开了）。
    /// </summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            var directory = _logDirectory();
            if (AppLogPaths.NewestLogFile(directory) is { } logFile)
            {
                _shell.Reveal(logFile);
                return;
            }
            if (Directory.Exists(directory))
            {
                _shell.OpenFolder(directory);
                return;
            }
            Status = $"日志目录不存在：{directory}";
        }
        catch (Exception ex)
        {
            Status = $"无法打开日志：{ex.Message}";
        }
    }

    private void OnLoaded(DbcDocument doc)
    {
        // DbcService.LoadAsync raises this event on its worker thread.
        // ObservableCollection<T>.CollectionChanged must fire on the UI
        // dispatcher when the collection is bound to an ItemsControl
        // (DataGrid, ListBox, etc.) — cross-thread mutation throws
        // NotSupportedException ("This type of CollectionView does not
        // support changes to its SourceCollection from a thread different
        // from the Dispatcher thread"). The marshal chokepoint lives in
        // DispatcherExtensions.RunOnUi; the previous Task 19 guard
        // (`appDispatcher == callingDispatcher`) was inverted and silently
        // skipped the hop in production. See
        // DispatcherExtensions.cs class doc-comment for the regression.
        ((Action)(() =>
        {
            Messages.Clear();
            _allMessages.Clear();
            FilteredMessages.Clear();
            foreach (var m in doc.Messages)
            {
                var vm = DbcMessageViewModel.From(m);
                Messages.Add(vm);
                _allMessages.Add(vm);
            }
            ApplyFilter();
            // Task 16: clear the decoded-signal table so stale entries
            // from a previous parse do not linger against a new DBC load.
            _signals.Reset();
            // v0.6.0: wire DBC service for value-table lookups.
            _signals.SetDbcService(_svc);
            TotalMessages = doc.Messages.Count;
            TotalSignals = doc.Messages.Sum(m => m.Signals.Count);
            var fileName = string.IsNullOrEmpty(LoadedPath) ? "(memory)" : Path.GetFileName(LoadedPath);
            Status = $"已加载 {TotalMessages} 条消息、{TotalSignals} 个信号：{fileName}";
            // 成功即撤销错误条：留着会让用户以为还坏着。
            LoadError = null;
        })).RunOnUi();
    }

    private void OnLoadFailed(PeakCan.HIL.Core.Error error)
    {
        // Same dispatcher marshaling rationale as OnLoaded. Status is
        // bound to the UI; marshal via the same chokepoint.
        ((Action)(() =>
        {
            Status = $"FAIL: {error.Code} {error.Message}";
            LoadError = DescribeLoadFailure(error);
            // 失败不是本类发起的（脚本 dbc.load 走 DbcService 直连），
            // _lastOpenPath 指向的是上一次界面打开的文件——留着会让「重试」把
            // 那个文件重新加载、覆盖当前文档。清掉并关掉两个动作。
            if (_uiLoadInFlight == 0 && _lastOpenPath is not null)
            {
                _lastOpenPath = null;
                NotifyRecoveryCommands();
            }
        })).RunOnUi();
    }

    /// <summary>
    /// 把 <see cref="PeakCan.HIL.Core.ErrorCode"/> 翻成一句人话，原始信息原样附上
    /// （排查要靠它，不能为了"好看"把原文裁掉）。
    /// </summary>
    private static string DescribeLoadFailure(PeakCan.HIL.Core.Error error) => error.Code switch
    {
        PeakCan.HIL.Core.ErrorCode.ParseFailure    => $"DBC 语法解析失败：{error.Message}",
        PeakCan.HIL.Core.ErrorCode.IoError         => $"读取文件失败：{error.Message}",
        PeakCan.HIL.Core.ErrorCode.NotFound        => $"文件不存在：{error.Message}",
        PeakCan.HIL.Core.ErrorCode.DbcFileTooLarge => $"文件超过大小上限：{error.Message}",
        _                                          => $"加载失败（{error.Code}）：{error.Message}",
    };
}