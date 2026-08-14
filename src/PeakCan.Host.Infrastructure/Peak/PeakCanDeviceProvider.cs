using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Devices;

namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// P1-1: <see cref="ICanDeviceProvider"/> for the PEAK PCAN-USB FD family.
/// The descriptor's channels come from <see cref="IChannelEnumerator"/>; the
/// bitrate capabilities are the static Core <see cref="BaudRate"/> presets.
/// New CAN boxes add their own provider — the UI needs no change.
/// </summary>
public sealed class PeakCanDeviceProvider : ICanDeviceProvider
{
    /// <summary>PCAN-USB FD first channel handle — default when enumeration
    /// finds no hardware (the channel layer probes this handle).</summary>
    public const ushort PcanUsbFdFirstHandle = 0x51;

    private static readonly BaudRate[] ClassicBaudRates =
    {
        BaudRate.Can125kbps,
        BaudRate.Can250kbps,
        BaudRate.Can500kbps,
        BaudRate.Can1Mbps,
    };

    private static readonly BaudRate[] FdBaudRates =
    {
        BaudRate.CanFd1Mbps,
        BaudRate.CanFd2Mbps,
        BaudRate.CanFd5Mbps,
    };

    private readonly IChannelEnumerator? _enumerator;
    private readonly ILogger<PeakCanDeviceProvider> _logger;

    public PeakCanDeviceProvider(IChannelEnumerator? enumerator, ILogger<PeakCanDeviceProvider>? logger = null)
    {
        _enumerator = enumerator;
        _logger = logger ?? NullLogger<PeakCanDeviceProvider>.Instance;
    }

    public IReadOnlyList<DeviceDescriptor> EnumerateDevices()
    {
        var channels = _enumerator?.Enumerate() ?? Array.Empty<ChannelInfo>();
        var descriptors = channels
            .Select(c => new ChannelDescriptor(c.Handle, c.Name))
            .ToList();
        var defaultHandle = descriptors.Count > 0 ? descriptors[0].Handle : PcanUsbFdFirstHandle;
        return new[]
        {
            new DeviceDescriptor(
                Id: "peak-usb-fd",
                DisplayName: "PCAN-USB FD (PEAK)",
                Channels: descriptors,
                BaudRates: ClassicBaudRates,
                SupportsFd: true,
                FdBaudRates: FdBaudRates,
                DefaultHandle: defaultHandle,
                DefaultBaudRate: BaudRate.Can500kbps),
        };
    }
}
