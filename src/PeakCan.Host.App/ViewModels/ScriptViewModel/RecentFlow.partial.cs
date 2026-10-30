using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.Ui;

namespace PeakCan.Host.App.ViewModels;

/// <summary>「最近打开」里的一项：<see cref="Label"/> 只显示文件名，<see cref="Path"/> 用于打开与提示。</summary>
public sealed record RecentScriptItem(string Path, string Label);

public sealed partial class ScriptViewModel
{
    // Flow: 脚本页「最近打开」。
    //
    // 与会话 MRU 分开存（%APPDATA%/PeakCan.Host/recent-scripts.json，见 RecentScriptsStore）：
    // 「文件 → 最近打开」那份菜单按**会话语义**打开文件，把脚本路径混进去会点出错误的行为。
    //
    // 打开与保存成功都会把路径置顶；列表为空时菜单项变灰，而不是给用户一个空菜单。

    /// <summary>最近打开过的脚本文件（最新在前）。</summary>
    public ObservableCollection<RecentScriptItem> RecentScripts { get; } = new();

    /// <summary>菜单项是否可用。空列表时置灰。</summary>
    public bool HasRecentScripts => RecentScripts.Count > 0;

    /// <summary>存储是否已装配（DI 漏注册时功能静默关闭，测试据此守卫）。</summary>
    internal bool HasRecentScriptsStore => _recentScripts is not null;

    /// <summary>从磁盘重读列表。启动时（AppShell 初始化）调用一次。</summary>
    internal void RefreshRecentScripts()
    {
        if (_recentScripts is null) return;
        ReplaceRecentScripts(_recentScripts.Load());
    }

    private void RememberScript(string path)
    {
        if (_recentScripts is null || string.IsNullOrWhiteSpace(path)) return;
        ReplaceRecentScripts(_recentScripts.Add(path));
    }

    private void ReplaceRecentScripts(IReadOnlyList<RecentScriptDto> entries)
    {
        RecentScripts.Clear();
        foreach (var entry in entries)
            RecentScripts.Add(new RecentScriptItem(entry.Path, Path.GetFileName(entry.Path)));
        OnPropertyChanged(nameof(HasRecentScripts));
    }

    /// <summary>
    /// 清空最近列表。删除失败时 UI 也按"已清空"处理——列表只是便利信息，
    /// 下次成功写入会覆盖那个文件。
    /// </summary>
    [RelayCommand]
    private void ClearRecentScripts()
    {
        _recentScripts?.Clear();
        ReplaceRecentScripts(Array.Empty<RecentScriptDto>());
    }

    /// <summary>从「最近打开」装载脚本。文件已被移走/删除要如实说，不能静默无反应。</summary>
    [RelayCommand]
    private async Task OpenRecentScriptAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!File.Exists(path))
        {
            StatusText = $"文件不存在：{path}";
            return;
        }

        await OpenPathAsync(path).ConfigureAwait(true);
    }
}
