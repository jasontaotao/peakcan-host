using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Composite;

/// <summary>
/// 组合通道探测：遍历所有注册的 <see cref="IChannelProbe"/>，
/// 返回第一个成功的探测结果。如果全部失败，返回最后一个失败的结果。
/// </summary>
public sealed class CompositeChannelProbe : IChannelProbe
{
    private readonly IChannelProbe[] _probes;

    public CompositeChannelProbe(IEnumerable<IChannelProbe> probes)
    {
        _probes = (probes ?? throw new ArgumentNullException(nameof(probes)))
            .Where(p => p.GetType() != typeof(CompositeChannelProbe))
            .ToArray();
    }

    public ProbeResult Probe(ushort handle)
    {
        ProbeResult? lastFailure = null;
        foreach (var probe in _probes)
        {
            var result = probe.Probe(handle);
            if (result.Ok)
                return result;
            lastFailure = result;
        }
        return lastFailure ?? new ProbeResult(false, "No probe available");
    }
}