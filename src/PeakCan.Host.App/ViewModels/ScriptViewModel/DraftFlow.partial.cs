using System.IO;
using System.Windows;
using System.Windows.Threading;
using PeakCan.Host.App.Services.Ui;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class ScriptViewModel
{
    // Flow: 脚本草稿持久化——编辑内容落盘，重启后自动恢复。
    //
    // 为什么需要它：切 tab 已经有草稿（ScriptView 每次重建页面都用 ScriptText 作初值），
    // 但关程序就没了。用户写了半小时的脚本、忘了点「保存」，退出确认点「是」就归零。
    //
    // 三条数据安全约束：
    //   1. **干净就不写**：只有 HasUnsavedChanges 时才落盘，否则清掉草稿。
    //      否则"上次的草稿"会在下次启动时把用户已经保存过的内容又翻出来。
    //   2. **恢复前先重建基线**：拿磁盘原文当 _savedText，脏状态才是真的
    //      （与 FileFlow.OpenAsync 同一条顺序约束，反了会把干净文件判脏）。
    //   3. **原文件读不到时不留路径**：默认编码是 UTF-8/LF，拿它去覆盖一个
    //      真实存在的 GBK/CRLF 文件是不可逆的数据损坏，宁可让用户另存。
    //
    // 落盘时机：输入静止 2 s（防抖）+ 退出时 flush。强杀进程最多丢 2 s 的输入。

    private const int DraftDebounceMilliseconds = 2000;

    /// <summary>防抖窗口。测试可以缩短；timer 是首次编辑时才创建的，所以 init 赋值有效。</summary>
    internal int DraftDebounceMs { get; init; } = DraftDebounceMilliseconds;

    private DispatcherTimer? _draftTimer;

    /// <summary>打开文件 / 恢复草稿期间抑制落盘（那是"装载"而不是"用户编辑"）。</summary>
    private bool _suppressDraftPersist;

    /// <summary>草稿存储是否已装配。DI 漏注册时草稿功能会静默关闭，测试据此守卫。</summary>
    internal bool HasDraftStore => _draftStore is not null;

    /// <summary>是否已排了一次防抖落盘（测试用：钉住"用户输入真的会触发调度"）。</summary>
    internal bool IsDraftPersistPending => _draftTimer is { IsEnabled: true };

    partial void OnScriptTextChanged(string value)
    {
        if (_draftStore is null || _suppressDraftPersist) return;
        ScheduleDraftPersist();
    }

    private void ScheduleDraftPersist()
    {
        _draftTimer ??= CreateDraftTimer();
        _draftTimer.Stop();
        _draftTimer.Start();
    }

    private DispatcherTimer CreateDraftTimer()
    {
        // 显式绑 Application.Current.Dispatcher：VM 由 DI 在 App.OnStartup 里解析，
        // 一旦将来有真正异步的 hosted service 参与启动，Dispatcher.CurrentDispatcher
        // 会指到线程池线程——timer 会静默地永不 tick。
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(DraftDebounceMs)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            PersistDraft();
        };
        return timer;
    }

    /// <summary>
    /// 把当前编辑内容写进草稿。timer tick、退出 flush、打开/保存成功后都走这里。
    /// 返回 <c>false</c> 表示这次没写成功（超限或 IO 失败），调用方要能看见。
    /// </summary>
    internal bool PersistDraft()
    {
        _draftTimer?.Stop();
        if (_draftStore is null) return true;

        // 干净时清草稿。删除失败**不算**"内容没保住"——不该让退出拦截对用户说
        // "这些改动会丢失"。但也不能留着旧文件：mtime 判旧只在"草稿带路径"时成立，
        // 而"从没绑过路径 → 另存为新文件"或"另存为到别的文件"这两种情况下，旧草稿
        // 会以未保存内容的形式复活。删不掉就用**空草稿**顶掉：Load() 对空文本返回
        // null，下次启动不会恢复任何东西。
        if (!HasUnsavedChanges)
        {
            if (!_draftStore.Clear())
                _draftStore.Save(new ScriptDraftDto("", null, DateTime.UtcNow.Ticks));
            return true;
        }

        if (ScriptText.Length > ScriptDraftStore.MaxDraftChars)
        {
            StatusText = $"脚本超过 {ScriptDraftStore.MaxDraftChars:N0} 字符，未保存草稿（请手动保存）";
            return false;
        }

        var saved = _draftStore.Save(
            new ScriptDraftDto(ScriptText, _currentPath, DateTime.UtcNow.Ticks));
        if (!saved) StatusText = "草稿保存失败（磁盘不可写？）";
        return saved;
    }

    /// <summary>启动时恢复上次未保存的草稿。</summary>
    internal void RestoreDraft()
    {
        if (_draftStore is null) return;
        // 防御：调用点若晚于用户开始编辑（或有别处已装载内容），不能整段覆盖。
        if (!string.IsNullOrWhiteSpace(ScriptText) || _currentPath is not null) return;

        bool pushToEditor;
        try
        {
            pushToEditor = RestoreDraftCore();
        }
        catch (Exception ex)
        {
            // 这是启动路径（AppShell.OnSourceInitialized）。草稿是外部数据：损坏的
            // JSON、非法路径、被占用的文件都能抛。逃出去会走到
            // DispatcherUnhandledException → 进程崩，而草稿还在 → **每次开机都崩**。
            // 宁可清掉草稿走空态，也不能让用户自己去 %APPDATA% 删文件。
            LogDraftRestoreFailed(_logger, ex);
            try { _draftStore.Clear(); } catch { /* 连删都失败：至少别再崩一次 */ }
            StatusText = "脚本草稿无法恢复，已清除";
            return;
        }

        // 推给编辑器放在 try 之外：文本已经恢复之后若这里抛（订阅者自己的 bug），
        // 不能被误判成"恢复失败"然后删掉草稿——那是崩溃后唯一的安全网。
        if (pushToEditor) EditorTextReplaceRequested?.Invoke(ScriptText);
    }

    /// <returns><c>true</c> = 恢复出了脏内容，需要整段推给编辑器。</returns>
    private bool RestoreDraftCore()
    {
        var store = _draftStore!;
        var draft = store.Load();
        if (draft is null || string.IsNullOrWhiteSpace(draft.Text)) return false;

        // 陈旧草稿：文件比草稿新（保存成功但清理草稿失败，或被外部编辑器改过）。
        // 拿旧草稿覆盖新文件是最坏的一种数据丢失。
        if (!string.IsNullOrEmpty(draft.Path) && IsSourceNewerThan(draft))
        {
            store.Clear();
            StatusText = "脚本文件比草稿新，未恢复草稿";
            return false;
        }

        string? baselineError = null;
        _suppressDraftPersist = true;
        try
        {
            if (!string.IsNullOrEmpty(draft.Path) && !TryLoadBaseline(draft.Path, out baselineError))
            {
                // 读不到原文件：未知编码 + 未知内容。绝不能保留路径走默认 UTF-8/LF，
                // 那会用错误的编码覆盖一个真实存在的文件。
                _savedText = "";
                _currentPath = null;
                _sourceWasLossy = true;
                OnPropertyChanged(nameof(CurrentFilePath));
            }

            ScriptText = NormalizeNewLines(draft.Text);
            OnPropertyChanged(nameof(HasUnsavedChanges));
        }
        finally
        {
            _suppressDraftPersist = false;
        }

        if (!HasUnsavedChanges)
        {
            // 与磁盘一致：草稿已经没有价值。
            store.Clear();
            return false;
        }

        if (baselineError is not null)
            StatusText = $"已恢复上次未保存的脚本（原文件不可读：{baselineError}，保存将另存为）";
        else if (_currentPath is null)
            StatusText = "已恢复上次未保存的脚本（未绑定文件，保存时会询问路径）";
        else
            StatusText = $"已恢复上次未保存的脚本：{Path.GetFileName(_currentPath)}";
        return true;
    }

    private static bool IsSourceNewerThan(ScriptDraftDto draft)
    {
        try
        {
            return File.Exists(draft.Path)
                   && File.GetLastWriteTimeUtc(draft.Path!).Ticks > draft.SavedAtUtcTicks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            // 判断不了就当"不是更新"：宁可恢复草稿（用户看得见内容），
            // 也不要因为一次 IO 抖动悄悄丢掉他的编辑。
            return false;
        }
    }
}
