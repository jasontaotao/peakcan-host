using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Zlg;

namespace PeakCan.Host.Infrastructure.Composite;

/// <summary>
/// 组合通道工厂：根据 <see cref="ChannelId.Handle"/> 范围路由到正确的子工厂。
/// PEAK 工厂处理 0x51-0x60，ZLG 工厂处理 0x0100+。
/// </summary>
public sealed class CompositeChannelFactory : IChannelFactory
{
    private readonly IChannelFactory[] _factories;

    public CompositeChannelFactory(IEnumerable<IChannelFactory> factories)
    {
        _factories = (factories ?? throw new ArgumentNullException(nameof(factories)))
            .Where(f => f.GetType() != typeof(CompositeChannelFactory))
            .ToArray();
    }

    public ICanChannel Create(ChannelId id)
    {
        foreach (var factory in _factories)
        {
            // 尝试创建：子工厂返回的通道如果 IsConnected 为 false 且
            // 不是该工厂的 handle 范围，应由子工厂自身处理。
            var channel = factory.Create(id);
            // 简单路由：按 handle 范围选择工厂
            if (IsHandleInRange(id.Handle, factory))
                return channel;
        }
        // fallback：交给第一个工厂
        return _factories[0].Create(id);
    }

    private static bool IsHandleInRange(ushort handle, IChannelFactory factory)
    {
        return factory switch
        {
            // PEAK 工厂：0x51-0x60
            PeakCan.Host.Infrastructure.Peak.PeakCanChannelFactory => handle >= 0x51 && handle <= 0x60,
            // ZLG 工厂：高 1 位为 1（0x8000+）
            ZlgCanChannelFactory => (handle & 0x8000) != 0,
            // 未知工厂类型，接受
            _ => true,
        };
    }
}