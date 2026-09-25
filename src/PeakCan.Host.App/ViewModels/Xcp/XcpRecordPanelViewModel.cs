using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using A2lEditor.Core;
using A2lEditor.Core.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core.Xcp.Record;

namespace PeakCan.Host.App.ViewModels.Xcp;

/// <summary>
/// MDF 记录面板 VM（S4-T5，spec D5）：挂在 XCP 采集面板的记录控件区。
/// <para>
/// 门禁与生命周期（D5 定案）：开始记录要求采集已运行（未运行禁用）；采集 Stop 自动
/// <b>先停记录再停采集</b>（尾部样本落盘——经 XcpAcquisitionPanelViewModel.BeforeStopAsync
/// 由组合根/编排器接线）；写盘故障 → 记录自停 + 状态区红字，采集不受影响
///（Core sink 故障隔离 + 广播层异常隔离，spec D4/D5）。
/// </para>
/// <para>
/// 通道清单 = 当前关注集卡片（对象名 + 合同单位），Start 时快照（关注集运行期动态）；
/// ContractSnapshot 经注入工厂（组合根接 Asap2PackageApi.ExportSnapshot，spec D2）
/// 每次 Start 落 AT 附件。状态行经 <see cref="RefreshState"/> 由视图层 20 Hz 节拍刷新
///（timer 不进 VM，T5 定案先例）。
/// </para>
/// </summary>
public partial class XcpRecordPanelViewModel : ObservableObject
{
    private readonly XcpMdfRecordSink? _sink;
    private readonly XcpAcquisitionPanelViewModel? _acquisition;
    private readonly XcpCardPanelViewModel? _cards;
    private readonly XcpConnectionPanelViewModel? _connection;
    private readonly Func<ContractSnapshot?>? _snapshotFactory;
    private readonly string _directory;

    /// <summary>可空注入构造（沿 App VM 测试构造先例：无参可建）。</summary>
    /// <param name="acquisition">采集面板（Start 门禁数据源）。</param>
    /// <param name="cards">卡片面板（Start 时快照关注集为通道清单）。</param>
    /// <param name="connection">连接面板（LoadedResult → 快照工厂默认来源）。</param>
    /// <param name="sink">Core 记录 sink（组合根注入共享实例）。</param>
    /// <param name="snapshotFactory">快照工厂（spec D2；缺省从 connection.LoadedResult 经包 API 导出）。</param>
    /// <param name="directory">记录目录（spec D5：默认会话目录，可改选）。</param>
    public XcpRecordPanelViewModel(
        XcpAcquisitionPanelViewModel? acquisition = null,
        XcpCardPanelViewModel? cards = null,
        XcpConnectionPanelViewModel? connection = null,
        XcpMdfRecordSink? sink = null,
        Func<ContractSnapshot?>? snapshotFactory = null,
        string? directory = null)
    {
        _acquisition = acquisition;
        _cards = cards;
        _connection = connection;
        _sink = sink;
        _snapshotFactory = snapshotFactory ?? BuildSnapshotFromConnection;
        _directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(AppContext.BaseDirectory, "recordings")
            : directory!;
        RecordDirectory = _directory;
    }

    /// <summary>Core 记录 sink（组合根广播装配的同一实例）。</summary>
    public XcpMdfRecordSink? Sink => _sink;

    /// <summary>记录目录（spec D5：可浏览改选；Start 时生效）。</summary>
    [ObservableProperty]
    private string _recordDirectory;

    /// <summary>是否记录中（Core sink 镜像）。</summary>
    [ObservableProperty]
    private bool _isRecording;

    /// <summary>记录文件全路径（Start 后非空）。</summary>
    [ObservableProperty]
    private string? _filePath;

    /// <summary>成功落盘条数。</summary>
    [ObservableProperty]
    private long _writtenCount;

    /// <summary>满队列 DropOldest 丢条数。</summary>
    [ObservableProperty]
    private long _droppedCount;

    /// <summary>未知对象样本条数（关注集外对象）。</summary>
    [ObservableProperty]
    private long _unknownSampleCount;

    /// <summary>记录时长（人读文本）。</summary>
    [ObservableProperty]
    private string _durationText = "00:00:00";

    /// <summary>最近一条启停事件（人读文本）。</summary>
    [ObservableProperty]
    private string? _statusText;

    /// <summary>写盘故障红字（IsFaulted 后非空，spec D5 状态区红字）。</summary>
    [ObservableProperty]
    private string? _faultText;

    partial void OnRecordDirectoryChanged(string value) => StartRecordCommand.NotifyCanExecuteChanged();

    private bool CanStartRecord() =>
        _sink is not null
        && !IsRecording
        && (_sink?.IsFaulted ?? false) == false
        && (_acquisition?.IsAcquiring ?? false)
        && (_cards?.Cards.Count ?? 0) > 0
        && !string.IsNullOrWhiteSpace(RecordDirectory);

    /// <summary>开始记录（D5 门禁：采集未运行禁用；通道=关注集快照；快照落附件）。</summary>
    [RelayCommand(CanExecute = nameof(CanStartRecord))]
    private async Task StartRecordAsync()
    {
        if (_sink is null || !CanStartRecord())
            return; // WPF 按钮走 CanExecute；直调 ExecuteAsync 在此被拦（同一门禁）。

        var channels = _cards!.Cards
            .Select(c => new MdfChannelSpec(c.Name, c.Contract.Unit))
            .ToList();

        var snapshot = _snapshotFactory!();
        await _sink.StartAsync(DateTimeOffset.Now, channels, snapshot).ConfigureAwait(true);

        IsRecording = true;
        FilePath = _sink.FilePath;
        FaultText = null;
        StatusText = $"记录已开始：{FilePath}";
        RefreshState();
    }

    /// <summary>停止记录（幂等；尾部样本由 Core sink 排空后 Finalize）。</summary>
    [RelayCommand(CanExecute = nameof(CanStopRecord))]
    private async Task StopRecordAsync()
    {
        if (_sink is null)
            return;
        await _sink.StopAsync().ConfigureAwait(true);
        AfterStop();
    }

    private bool CanStopRecord() => _sink is not null && IsRecording;

    /// <summary>
    /// 采集 Stop 的先停记录口（spec D5：先停记录再停采集）。组合根经
    /// XcpAcquisitionPanelViewModel.BeforeStopAsync 接线；记录未运行时零开销直返。
    /// </summary>
    public async Task StopBeforeAcquisitionAsync()
    {
        if (_sink is null || !IsRecording)
            return;
        await _sink.StopAsync().ConfigureAwait(true);
        AfterStop();
        StatusText = "记录已随采集停止而停止（尾部样本已落盘）。";
    }

    /// <summary>
    /// 视图层 20 Hz 节拍刷新（timer 不进 VM，T5 先例）：同步 sink 状态面到 UI 可观察属性。
    /// 写盘故障 → 记录自停 + 状态区红字（IsFaulted 行），采集不受影响（广播隔离）。
    /// </summary>
    public void RefreshState()
    {
        if (_sink is null)
            return;

        IsRecording = _sink.IsRecording && !_sink.IsFaulted; // 故障即自停（spec D5）
        FilePath = _sink.FilePath;
        WrittenCount = _sink.WrittenCount;
        DroppedCount = _sink.DroppedCount;
        UnknownSampleCount = _sink.UnknownSampleCount;
        DurationText = _sink.Duration.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);

        if (_sink.IsFaulted)
            FaultText = $"记录写入器故障，记录已自停（采集不受影响）：{_sink.LastError?.Message}";
        StartRecordCommand.NotifyCanExecuteChanged();
        StopRecordCommand.NotifyCanExecuteChanged();
    }

    private void AfterStop()
    {
        IsRecording = false;
        RefreshState();
        StartRecordCommand.NotifyCanExecuteChanged();
        StopRecordCommand.NotifyCanExecuteChanged();
    }

    /// <summary>组合根缺省快照工厂（spec D2）：LoadedResult → 包 API 导出。</summary>
    private ContractSnapshot? BuildSnapshotFromConnection()
    {
        var loaded = _connection?.LoadedResult;
        if (loaded is null)
            return null;
        var plan = AcquisitionPlan.Build(loaded.Document);
        return Asap2PackageApi.ExportSnapshot(loaded.Document, plan);
    }
}
