namespace PeakCan.HIL.Core.Devices;

/// <summary>
/// Adapter-independent discovery of CAN hardware. Each supported device
/// family (PEAK today, others later) is one provider; DI registers them
/// all and consumers enumerate the union. Non-throwing: a provider returns
/// an empty list when none of its hardware is present, and a device with
/// empty <see cref="DeviceDescriptor.Channels"/> when hardware is present
/// but no channel responded.
/// </summary>
public interface ICanDeviceProvider
{
    IReadOnlyList<DeviceDescriptor> EnumerateDevices();
}
