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
        // 先按 handle 范围选工厂，再构造（review LOW：原来先构造再检查，对不匹配工厂
        // 也 Create 一次——多通道/单通道现在全走本分派是热路径，且未来厂商工厂若
        // Create 有副作用（如开设备）会 double-open/泄漏）。
        foreach (var factory in _factories)
        {
            if (IsHandleInRange(id.Handle, factory))
                return factory.Create(id);
        }
        // fallback：交给第一个工厂（空 handle 兜底 0x51+index 语义）
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