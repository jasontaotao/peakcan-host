namespace PeakCan.HIL.Core.Devices;

/// <summary>One CAN channel offered by a device: raw handle + UI name.</summary>
public sealed record ChannelDescriptor(ushort Handle, string Name);

/// <summary>
/// Device-independent description of an available CAN adapter, driving the
/// connection-settings UI. A new CAN box = a new <see cref="ICanDeviceProvider"/>
/// emitting its own descriptor; the UI binds these fields with no code change.
/// <para>
/// <c>BaudRates</c> are the classic (non-FD) presets; <c>FdBaudRates</c> are
/// the FD data-phase rates shown when FD mode is enabled. Both reuse the
/// Core <see cref="BaudRate"/> model (which the channel layer already maps
/// to PEAK descriptors).
/// </para>
/// </summary>
public sealed record DeviceDescriptor(
    string Id,
    string DisplayName,
    IReadOnlyList<ChannelDescriptor> Channels,
    IReadOnlyList<BaudRate> BaudRates,
    bool SupportsFd,
    IReadOnlyList<BaudRate> FdBaudRates,
    ushort DefaultHandle,
    BaudRate DefaultBaudRate);
