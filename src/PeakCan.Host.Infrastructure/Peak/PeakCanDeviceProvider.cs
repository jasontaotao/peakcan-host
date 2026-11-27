using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Devices;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Composite;

namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// P1-1: <see cref="ICanDeviceProvider"/> for the PEAK PCAN-USB FD family.
/// The descriptor's channels come from <see cref="IChannelEnumerator"/>; the
/// bitrate capabilities are the static Core <see cref="BaudRate"/> presets.
/// New CAN boxes add their own provider — the UI needs no change.
/// </summary>
public sealed class PeakCanDeviceProvider : ICanDeviceProvider
{
    /// <summary>PCAN-USB FD first channel handle. Enumeration no longer falls
    /// back to it: advertising a handle without hardware made a ZLG-only setup
    /// look like a connected PEAK box.</summary>
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
        // 复合枚举器会把 ZLG 通道也报成 PEAK 通道，导致只插 ZLG 时
        // 界面仍默认选中 PEAK，并触发 PEAK 驱动初始化。装配错误要尽早暴露。
        if (enumerator is CompositeChannelEnumerator)
        {
            throw new ArgumentException(
                "PeakCanDeviceProvider requires PeakChannelEnumerator; a composite " +
                "enumerator would report ZLG channels as PEAK channels.",
                nameof(enumerator));
        }

        _enumerator = enumerator;
        _logger = logger ?? NullLogger<PeakCanDeviceProvider>.Instance;
    }

    /// <summary>Test seam: which enumerator was wired in.</summary>
    internal IChannelEnumerator? Enumerator => _enumerator;

    public IReadOnlyList<DeviceDescriptor> EnumerateDevices()
    {
        var channels = _enumerator?.Enumerate() ?? Array.Empty<ChannelInfo>();
        var descriptors = channels
            .Select(c => new ChannelDescriptor(c.Handle, c.Name))
            .ToList();
        // 无硬件时不再伪造 0x51：UI 会把 DefaultHandle != 0 当成"设备可用"，
        // 只插了 ZLG 的用户会误以为 PEAK 也在线，进而初始化 PEAK 驱动。
        var defaultHandle = descriptors.Count > 0 ? descriptors[0].Handle : (ushort)0;
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
