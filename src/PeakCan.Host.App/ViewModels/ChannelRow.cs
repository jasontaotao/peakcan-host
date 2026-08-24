using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Devices;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// Task 4 (phase 2 A-2): one row in the multi-channel connection-settings list.
/// Self-contained: each row independently drives its own device → channel →
/// FD → baud-rate selection (mirroring the legacy single-group
/// <see cref="ConnectionSettingsViewModel"/> field logic). The VM's legacy
/// single-group fields delegate to <c>Rows[0]</c> so existing single-channel
/// callers/tests keep working (zero regression).
/// </summary>
public sealed partial class ChannelRow : ObservableObject
{
    private static readonly IReadOnlyList<BaudRate> EmptyBaudRates = Array.Empty<BaudRate>();

    // Devices list is shared across rows (same hardware enumeration); each row
    // picks one. Read-only reference from the parent VM.
    public IReadOnlyList<DeviceDescriptor> Devices { get; }

    public ChannelRow(IReadOnlyList<DeviceDescriptor> devices)
    {
        Devices = devices ?? Array.Empty<DeviceDescriptor>();
        if (Devices.Count > 0)
            _selectedDevice = Devices[0];
        // Mirror the legacy VM ctor: first device selected → drive channels/FD.
        if (_selectedDevice is { } dev)
        {
            _channels = dev.Channels;
            if (_channels.Count > 0)
                _selectedChannel = _channels[0];
            _isFd = dev.SupportsFd;
            RefreshBaudRates();
        }
    }

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

    /// <summary>Label for the bitrate dropdown — "数据段速率" in FD mode.</summary>
    public string RateLabel => IsFd ? "数据段速率" : "波特率";

    partial void OnSelectedDeviceChanged(DeviceDescriptor? value)
    {
        if (value is null)
        {
            Channels = Array.Empty<ChannelDescriptor>();
            return;
        }
        Channels = value.Channels;
        if (Channels.Count > 0)
            SelectedChannel = Channels[0];
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
            SelectedBaudRate = list[0];
    }

    /// <summary>
    /// Resolve this row's selected channel to a <see cref="ChannelInfo"/> by
    /// matching the handle against the sink's AvailableChannels (null if no
    /// match — the shell's best-effort loop skips null-channel groups).
    /// </summary>
    public ChannelInfo? MatchChannel(IReadOnlyList<ChannelInfo> available)
        => available.FirstOrDefault(c => c.Handle == SelectedChannel?.Handle);
}
