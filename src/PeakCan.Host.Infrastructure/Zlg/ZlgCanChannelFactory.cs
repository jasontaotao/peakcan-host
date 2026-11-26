using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>
/// ZLG 通道工厂。从 <see cref="ChannelId.Handle"/> 解码设备类型/索引/通道索引；
/// 生产环境 DI 通过 <see cref="Composite.CompositeChannelFactory"/> 路由到本工厂。
/// </summary>
public sealed class ZlgCanChannelFactory : IChannelFactory
{
    private readonly ZlgDeviceManager _deviceManager;
    private readonly IZlgApi _api;
    private readonly ILogger<ZlgCanChannel> _logger;

    public ZlgCanChannelFactory(
        ZlgDeviceManager deviceManager,
        IZlgApi api,
        ILogger<ZlgCanChannel>? logger = null)
    {
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _logger = logger ?? NullLogger<ZlgCanChannel>.Instance;
    }

    public ICanChannel Create(ChannelId id)
        => new ZlgCanChannel(id, _deviceManager, _api, _logger);
}
