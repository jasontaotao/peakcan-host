using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Devices;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>
/// ZLG 设备 Provider。实现 <see cref="ICanDeviceProvider"/>，
/// 枚举 ZLG USBCAN FD 设备并返回设备描述符。
/// <see cref="ConnectionSettingsViewModel"/> 通过 <see cref="IEnumerable{T}"/>
/// 自动发现所有 provider，无需 UI 改动。
/// </summary>
public sealed class ZlgCanDeviceProvider : ICanDeviceProvider
{
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

    private readonly IChannelEnumerator _enumerator;
    private readonly ILogger<ZlgCanDeviceProvider> _logger;

    public ZlgCanDeviceProvider(
        IChannelEnumerator enumerator,
        ILogger<ZlgCanDeviceProvider>? logger = null)
    {
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ZlgCanDeviceProvider>.Instance;
    }

    public IReadOnlyList<DeviceDescriptor> EnumerateDevices()
    {
        var channels = _enumerator.Enumerate();
        var descriptors = channels
            .Select(c => new ChannelDescriptor(c.Handle, c.Name))
            .ToList();

        // 始终返回设备描述符（即使无通道），让 UI 显示设备类型。
        // 通道列表为空时用户仍能看到 "USBCAN FD (ZLG)" 选项，但无可用通道；
        // DefaultHandle 此时为 0（无有效通道），避免伪造一个不存在的 handle。
        var defaultHandle = descriptors.Count > 0 ? descriptors[0].Handle : (ushort)0;
        return new[]
        {
            new DeviceDescriptor(
                Id: "zlg-usbcan-fd",
                DisplayName: "USBCAN FD (ZLG)",
                Channels: descriptors,
                BaudRates: ClassicBaudRates,
                SupportsFd: true,
                FdBaudRates: FdBaudRates,
                DefaultHandle: defaultHandle,
                DefaultBaudRate: BaudRate.Can500kbps),
        };
    }

}