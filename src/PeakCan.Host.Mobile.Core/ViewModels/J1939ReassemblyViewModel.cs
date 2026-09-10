using System.Collections.ObjectModel;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Core.ViewModels;

/// <summary>
/// J1939 tab 的行集合。重组事件从 player 线程引发，经 UI dispatcher Post
/// 追加到 <see cref="Rows"/>（ObservableCollection 仅 UI 线程变更）。
/// </summary>
public sealed class J1939ReassemblyViewModel
{
    private readonly IUiDispatcher _ui;

    public J1939ReassemblyViewModel(IUiDispatcher ui)
        => _ui = ui ?? throw new ArgumentNullException(nameof(ui));

    public ObservableCollection<J1939ReassembledRow> Rows { get; } = [];

    /// <summary>订阅重组事件；调用方负责在不需要时（如换 session）解绑。</summary>
    public void Attach(StreamingJ1939Reassembler reassembler)
        => reassembler.MessageReassembled += row => _ui.Post(() => Rows.Add(row));

    /// <summary>清空列表（换文件/重播时由 SessionVM 调用）。</summary>
    public void Clear() => _ui.Post(Rows.Clear);
}