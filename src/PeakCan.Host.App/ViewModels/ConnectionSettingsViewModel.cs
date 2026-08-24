using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Devices;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// P1-2: what the connection-settings panel writes back to the shell.
/// Implemented by <see cref="AppShellViewModel"/> (which already owns the
/// channel/baud/FD state + Connect flow) — an interface so the panel VM is
/// unit-testable without constructing a real shell, and to avoid a DI
/// circular dependency (shell ⇄ panel VM).
/// <para>
/// Task 2 (phase 2 A-1): multi-channel list contract. <see cref="ApplyConnections"/>
/// takes a list of <see cref="ConnectionConfig"/> (one per CAN channel); the
/// legacy single-group <see cref="ApplyConnection"/> is kept as a default
/// interface method that forwards to the list form with a single element —
/// zero regression for existing single-channel callers/tests.
/// </para>
/// </summary>
public interface IConnectSettingsSink
{
    IReadOnlyList<ChannelInfo> AvailableChannels { get; }

    /// <summary>Ensure channels are enumerated (shell probes hardware on
    /// first use) so a channel chosen here can be matched to a handle.</summary>
    void ProbeChannels();

    /// <summary>
    /// 多通道列表形式（phase 2 A-1）。shell 逐组尽力式连接，失败该组跳过不阻塞其余。
    /// </summary>
    void ApplyConnections(IReadOnlyList<ConnectionConfig> configs);

    /// <summary>
    /// 旧单组形式（DIM 默认转发到 <see cref="ApplyConnections"/>，单元素）——
    /// 零回归：既有单通道调用方/测试行为不变。channel 可空（对齐旧契约）：
    /// 始终转发单元素列表（含 null Channel），让 sink 回写 SelectedChannel=null
    /// 旧行为；ConnectAsync 循环中 <c>cfg.Channel is null continue</c> 跳过。
    /// （review H2 fix：旧空列表转发使 ApplyConnections 的 Count>0 守卫不触发，
    /// SelectedChannel 不被置 null → 工具栏残留旧选择。）
    /// </summary>
    void ApplyConnection(ChannelInfo? channel, BaudRate baudRate, bool isFd)
        => ApplyConnections(new[] { new ConnectionConfig(channel, baudRate, isFd) });

    /// <summary>Trigger the shell's Connect flow (no-op when not connectable).</summary>
    void Connect();
}

/// <summary>
/// Task 2 (phase 2 A-1): one CAN channel's connection parameters.
/// <see cref="Channel"/> is nullable to align with the legacy
/// <see cref="IConnectSettingsSink.ApplyConnection"/> contract (null channel
/// means "skip this group" — the shell's best-effort loop ignores it).
/// </summary>
public sealed record ConnectionConfig(ChannelInfo? Channel, BaudRate BaudRate, bool IsFd);

/// <summary>
/// P1-2/D6: connection-settings panel view model. Fields are driven by the
/// selected <see cref="DeviceDescriptor"/> (from <see cref="ICanDeviceProvider"/>),
/// so a new CAN box needs no UI change. "应用并连接" writes the selection back
/// through <see cref="IConnectSettingsSink"/> and triggers Connect.
/// </summary>
public partial class ConnectionSettingsViewModel : ObservableObject
{
    private static readonly IReadOnlyList<BaudRate> EmptyBaudRates = Array.Empty<BaudRate>();

    private readonly IConnectSettingsSink _sink;
    private readonly ILogger<ConnectionSettingsViewModel> _logger;

    public ConnectionSettingsViewModel(
        IEnumerable<ICanDeviceProvider> providers,
        IConnectSettingsSink sink,
        ILogger<ConnectionSettingsViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _logger = logger ?? NullLogger<ConnectionSettingsViewModel>.Instance;

        Devices = providers.SelectMany(p => p.EnumerateDevices()).ToList();
        if (Devices.Count > 0)
        {
            SelectedDevice = Devices[0];
        }
        // Ensure the shell can match the chosen channel handle to a real
        // channel (first open may not have probed yet).
        _sink.ProbeChannels();
    }

    /// <summary>Label for the bitrate dropdown — "数据段速率" in FD mode
    /// (the list is FD data-phase rates), "波特率" otherwise.</summary>
    public string RateLabel => IsFd ? "数据段速率" : "波特率";

    [ObservableProperty]
    private IReadOnlyList<DeviceDescriptor> _devices = Array.Empty<DeviceDescriptor>();

    [ObservableProperty]
    private DeviceDescriptor? _selectedDevice;

    [ObservableProperty]
    private IReadOnlyList<ChannelDescriptor> _channels = Array.Empty<ChannelDescriptor>();

    [ObservableProperty]
    private ChannelDescriptor? _selectedChannel;

    // Default true aligns with AppShellViewModel._isFd so an ApplyAndConnect
    // before any device-driven OnSelectedDeviceChanged (or for a device that
    // supports FD) does not silently flip the shell's FD mode to false.
    // OnSelectedDeviceChanged still overrides per device capability.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateLabel))]
    private bool _isFd = true;

    [ObservableProperty]
    private IReadOnlyList<BaudRate> _availableBaudRates = EmptyBaudRates;

    [ObservableProperty]
    private BaudRate _selectedBaudRate;

    partial void OnSelectedDeviceChanged(DeviceDescriptor? value)
    {
        if (value is null)
        {
            Channels = Array.Empty<ChannelDescriptor>();
            return;
        }
        Channels = value.Channels;
        if (Channels.Count > 0)
        {
            SelectedChannel = Channels[0];
        }
        // Capability-driven: FD on by default when the device supports it.
        IsFd = value.SupportsFd;
        RefreshBaudRates();
    }

    partial void OnIsFdChanged(bool value) => RefreshBaudRates();

    private void RefreshBaudRates()
    {
        var list = IsFd
            ? (SelectedDevice?.FdBaudRates ?? EmptyBaudRates)
            : (SelectedDevice?.BaudRates ?? EmptyBaudRates);
        AvailableBaudRates = list;
        if (list.Count > 0)
        {
            SelectedBaudRate = list[0];
        }
    }

    /// <summary>
    /// Task 4 (phase 2 A-2): extra channel rows beyond the first (the first
    /// group is still the legacy single-group fields above, so existing
    /// single-channel callers/tests keep working). Each AddChannel appends a
    /// row; RemoveChannel drops one.
    /// </summary>
    public ObservableCollection<ChannelRow> ExtraRows { get; } = new();

    [RelayCommand]
    private void AddChannel()
    {
        // 新行复用同一设备枚举（多 CAN 盒混插时用户可改每行设备）。
        ExtraRows.Add(new ChannelRow(Devices));
    }

    [RelayCommand]
    private void RemoveChannel(ChannelRow? row)
    {
        if (row is not null)
            ExtraRows.Remove(row);
    }

    [RelayCommand]
    private void ApplyAndConnect()
    {
        // Task 4: collect the first group (legacy single-group fields) + any
        // extra rows into a ConnectionConfig list → ApplyConnections. The
        // shell's best-effort loop connects each; legacy single-channel path
        // (no extra rows) yields a 1-element list, behaviorally equivalent to
        // the pre-T4 ApplyConnection call (DIM default forwards single→list).
        var available = _sink.AvailableChannels;
        var configs = new List<ConnectionConfig>
        {
            // 首组：既有单组字段
            new(_sink.AvailableChannels.FirstOrDefault(c => c.Handle == SelectedChannel?.Handle),
                SelectedBaudRate, IsFd),
        };
        // 额外行：各自匹配 handle
        foreach (var row in ExtraRows)
            configs.Add(new ConnectionConfig(row.MatchChannel(available), row.SelectedBaudRate, row.IsFd));
        _sink.ApplyConnections(configs);
        _sink.Connect();
    }
}
