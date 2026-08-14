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
/// </summary>
public interface IConnectSettingsSink
{
    IReadOnlyList<ChannelInfo> AvailableChannels { get; }

    /// <summary>Ensure channels are enumerated (shell probes hardware on
    /// first use) so a channel chosen here can be matched to a handle.</summary>
    void ProbeChannels();

    void ApplyConnection(ChannelInfo? channel, BaudRate baudRate, bool isFd);

    /// <summary>Trigger the shell's Connect flow (no-op when not connectable).</summary>
    void Connect();
}

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateLabel))]
    private bool _isFd;

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

    [RelayCommand]
    private void ApplyAndConnect()
    {
        var match = _sink.AvailableChannels.FirstOrDefault(c => c.Handle == SelectedChannel?.Handle);
        _sink.ApplyConnection(match, SelectedBaudRate, IsFd);
        _sink.Connect();
    }
}
