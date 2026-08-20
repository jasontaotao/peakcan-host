using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>
/// ZLG 通道工厂。从 <see cref="ChannelId.Handle"/> 解码设备类型/索引/通道索引。
/// 生产环境 DI 通过 <see cref="CompositeChannelFactory"/> 路由到本工厂。
/// </summary>
public sealed class ZlgCanChannelFactory : IChannelFactory
{
    private readonly ZlgDeviceManager _deviceManager;
    private readonly ILogger<ZlgCanChannel> _logger;
    private readonly IZlgReader _reader;

    public ZlgCanChannelFactory(
        ZlgDeviceManager deviceManager,
        ILogger<ZlgCanChannel>? logger = null,
        IZlgReader? reader = null)
    {
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ZlgCanChannel>.Instance;
        _reader = reader ?? new ZlgReader();
    }

    public ICanChannel Create(ChannelId id)
        => new ZlgCanChannel(id, _deviceManager, _logger, _reader);
}