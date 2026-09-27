using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using A2lEditor.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core.Xcp.Capability;

namespace PeakCan.Host.App.ViewModels.Xcp;

/// <summary>
/// XCP 连接面板状态机（spec D6/Q2 定案）。三态：Disconnected → Loaded → Connected。
/// <para>
/// 为什么中间态是 Loaded：A2L 声明侧加载成功 ≠ 总线已连——T7 采集面板在
/// CONNECT + 能力对账通过后才推进到 Connected；连接面板只负责声明侧加载
/// 与通道选择（Q2：通道仅来自 <see cref="IConnectedChannelsSource"/> 快照，
/// 本面板不做独立连接控件，避免双连接状态源）。
/// </para>
/// </summary>
public enum XcpConnectionState
{
    /// <summary>未加载 A2L（或加载失败）。</summary>
    Disconnected,

    /// <summary>A2L 声明侧已加载，未发起 XCP CONNECT。</summary>
    Loaded,

    /// <summary>XCP CONNECT 成功（生产者：T7 采集生命周期，经 MarkConnected）。</summary>
    Connected,
}

/// <summary>
/// D5 合并归因表（spec §2-D5）host 侧两格。host 态不进记录文件（S4 口径不动）。
/// </summary>
public enum XcpHostAttributionCell
{
    /// <summary>
    /// D5 表"未连总线"格。生产者：XcpConnectionPanelViewModel——
    /// ConnectionState != Connected 时所有对象归因到该格。
    /// </summary>
    UnconnectedBus,

    /// <summary>
    /// D5 表"被过滤"格。生产者是关注集筛选（T5 卡片过滤），连接面板不生产，
    /// 列在此处只为让 D5 host 侧枚举完整、上层可按同一枚举消费。
    /// </summary>
    FilteredOut,
}

/// <summary>
/// XCP 连接面板 VM（S3-T3）：A2L 选择路径 + 通道快照 + 连接状态机 + 状态区。
/// <para>
/// 不直接弹 WPF 对话框（照 host VM 可测先例）：A2L 路径由上层
///（T8 视图接线走 IFileDialogService 同款）写入 <see cref="A2lPath"/>，
/// 加载动作经注入委托执行，默认 <see cref="XcpA2lLoader.Load"/>。
/// </para>
/// </summary>
public partial class XcpConnectionPanelViewModel : ObservableObject
{
    /// <summary>P2-5：A2L 加载成功（含 Loaded→Loaded 重载）显式出站——MAP 清单重装信号。</summary>
    public event Action? A2lLoaded;

    private readonly Func<string, XcpA2lLoadResult> _loadA2l;
    private readonly IConnectedChannelsSource? _connectedChannels;

    /// <summary>
    /// 可空注入构造（保既有 VM 测试构造模式：无参可建、测试点零回归）。
    /// </summary>
    /// <param name="loadA2l">A2L 加载委托；缺省用 Core 的 XcpA2lLoader.Load（D4 单源）。</param>
    /// <param name="connectedChannels">已连接通道快照源（Q2）；缺省 = 无快照，通道列表恒空。</param>
    public XcpConnectionPanelViewModel(
        Func<string, XcpA2lLoadResult>? loadA2l = null,
        IConnectedChannelsSource? connectedChannels = null)
    {
        _loadA2l = loadA2l ?? XcpA2lLoader.Load;
        _connectedChannels = connectedChannels;
        if (_connectedChannels is not null)
        {
            _connectedChannels.Changed += RefreshChannels;
            RefreshChannels();
        }
    }

    /// <summary>A2L 文件路径（上层对话框选择后写入）。</summary>
    [ObservableProperty]
    private string _a2lPath = string.Empty;

    /// <summary>连接状态机当前态（形状见 <see cref="XcpConnectionState"/>）。</summary>
    [ObservableProperty]
    private XcpConnectionState _connectionState = XcpConnectionState.Disconnected;

    /// <summary>当前选中通道（仅可选 <see cref="Channels"/> 内条目，来源即快照）。</summary>
    [ObservableProperty]
    private HilViewModel.ConnectedChannel? _selectedChannel;

    /// <summary>
    /// Start 允许标志（供 T7 采集面板消费）：A2L 已加载且通道已选。
    /// 空快照 / 未选通道 / 未加载 / 已断开 → false。
    /// </summary>
    [ObservableProperty]
    private bool _canStart;

    /// <summary>已连接通道快照的只读投影（Q2：唯一通道来源，刷新自 IConnectedChannelsSource）。</summary>
    public ObservableCollection<HilViewModel.ConnectedChannel> Channels { get; } = new();

    /// <summary>状态区行（ValidationNote 渲染 / 加载结果 / 失败提示，人读文本）。</summary>
    public ObservableCollection<string> StatusLines { get; } = new();

    /// <summary>最近一次成功加载的 ValidationNote（与包 API CollectCrossChecks 同源）。</summary>
    public IReadOnlyList<ValidationNote> ValidationNotes { get; private set; } =
        Array.Empty<ValidationNote>();

    /// <summary>
    /// 最近一次成功加载的完整结果（Document/IfData/Contracts——T7 对账与
    /// ResolveBaudRate 的声明侧输入）。失败时不保留旧值语义见 LoadA2L。
    /// </summary>
    public XcpA2lLoadResult.Loaded? LoadedResult { get; private set; }

    /// <summary>
    /// D5 表 host 格归因：未连接态（Disconnected/Loaded）→ UnconnectedBus（"未连总线"格）；
    /// Connected → null（该态由包侧 Receive 归因接管，本格退场）。
    /// 上层（T6 归因面板）消费此属性，禁止自判状态机。
    /// </summary>
    public XcpHostAttributionCell? AttributionCell =>
        ConnectionState == XcpConnectionState.Connected
            ? null
            : XcpHostAttributionCell.UnconnectedBus;

    partial void OnConnectionStateChanged(XcpConnectionState value)
    {
        OnPropertyChanged(nameof(AttributionCell));
        UpdateCanStart();
        // T8 评审移交（T10 落地）：Connected 态禁用 LoadA2L 的门下沉到 VM
        // CanExecute——视图层 IsEnabled 只覆盖单按钮实例，VM 层门对任何
        // 未来命令宿主（快捷键/菜单/第二视图）同样生效。
        LoadA2LCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedChannelChanged(HilViewModel.ConnectedChannel? value) => UpdateCanStart();

    /// <summary>
    /// 加载 A2L 声明侧（成功 → Loaded；三类失败均只进状态区，不裸抛）。
    /// <para>T8 评审移交（T10）：Connected 态 CanExecute=false——重载成功会把
    /// 状态降回 Loaded，归因格存在说谎窗口期（T3 评审 LOW-3）。主门在 VM
    ///（CanExecute），XcpView 的 DataTrigger 保留作视图层双保险。</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadA2l))]
    private void LoadA2L()
    {
        var path = A2lPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowStatus("未选择 A2L 文件。");
            return;
        }

        // 第三类失败预检（T2 评审 LOW）：XcpA2lLoader.Load 不捕获 IO 异常，
        // 文件不存在时先拦下，不进委托。
        if (!File.Exists(path))
        {
            ShowStatus($"A2L 文件不存在: {path}");
            return;
        }

        XcpA2lLoadResult result;
        try
        {
            result = _loadA2l(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 第三类失败：IO 权限/占用类异常进状态区（不裸抛穿 UI）。
            ShowStatus($"A2L 读取失败: {ex.Message}");
            return;
        }

        switch (result)
        {
            case XcpA2lLoadResult.Loaded loaded:
            {
                LoadedResult = loaded;
                ValidationNotes = loaded.ValidationNotes;
                StatusLines.Clear();
                foreach (var note in loaded.ValidationNotes)
                    StatusLines.Add(FormatNote(note));
                StatusLines.Add($"A2L 加载成功: {loaded.Contracts.All.Count} 个合同对象。");
                ConnectionState = XcpConnectionState.Loaded;
                // P2-5：Loaded→Loaded 重载不触发 PropertyChanged——显式事件通知
                // MAP 清单重装（XcpViewModel 订阅）。
                A2lLoaded?.Invoke();
                break;
            }

            case XcpA2lLoadResult.Failed failed:
                // 第一类失败：显式 Result（D4 定案）。不清 LoadedResult / 不降状态机——
                // 重载失败不得清掉已加载的好状态。
                ShowStatus($"A2L 加载失败 [{failed.Kind}]: {failed.Message}");
                break;

            default:
                ShowStatus($"A2L 加载返回未知结果类型: {result.GetType().Name}");
                break;
        }
    }

    /// <summary>
    /// XCP CONNECT 成功回调（生产者：T7 采集生命周期；连接面板自身不发起连接）。
    /// </summary>
    public void MarkConnected() => ConnectionState = XcpConnectionState.Connected;

    // T10：LoadA2L 的 CanExecute 门（Connected 态禁用，防归因格说谎窗口期）。
    private bool CanLoadA2l() => ConnectionState != XcpConnectionState.Connected;

    /// <summary>
    /// 断开回调（T7 Stop / 通道快照清空时驱动）。A2L 仍有效（LoadedResult 非空）→
    /// 回 Loaded——D6 的 Stop 不要求重载 A2L，重连后须能直接再次 Start，不得推进
    /// "必须重新 LoadA2L"死胡同（T3 评审 MEDIUM-1）；否则回 Disconnected。两态 D5
    /// 归因格同为 UnconnectedBus，归因语义零变化。
    /// </summary>
    public void MarkDisconnected() =>
        ConnectionState = LoadedResult is not null
            ? XcpConnectionState.Loaded
            : XcpConnectionState.Disconnected;

    private void RefreshChannels()
    {
        var snapshot = _connectedChannels?.Current ?? Array.Empty<HilViewModel.ConnectedChannel>();
        var selectionInvalid = SelectedChannel is { } selected && !SnapshotContains(snapshot, selected);

        Channels.Clear();
        foreach (var channel in snapshot)
            Channels.Add(channel);

        if (selectionInvalid)
            SelectedChannel = null;
        else
            UpdateCanStart();
    }

    private static bool SnapshotContains(
        IReadOnlyList<HilViewModel.ConnectedChannel> snapshot,
        HilViewModel.ConnectedChannel channel) =>
        // 快照语义下按值身份（Handle/Name）比对：重连会新建 ICanChannel 实例，
        // record 默认相等（含引用成员比较）会把同口通道误判失效（T3 评审 LOW-1）。
        snapshot.Count > 0 && snapshot.Any(c =>
            c.Handle == channel.Handle && c.Name == channel.Name);

    private void UpdateCanStart() =>
        CanStart = ConnectionState is XcpConnectionState.Loaded or XcpConnectionState.Connected
            && SelectedChannel is not null;

    private void ShowStatus(string line)
    {
        StatusLines.Clear();
        StatusLines.Add(line);
    }

    private static string FormatNote(ValidationNote note) =>
        $"[{note.Severity}] {note.Kind}: {note.Message} (行 {string.Join(", ", note.Lines)})";
}
