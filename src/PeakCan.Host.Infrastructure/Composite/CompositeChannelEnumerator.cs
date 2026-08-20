using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Composite;

/// <summary>
/// 组合通道枚举器：遍历所有注册的 <see cref="IChannelEnumerator"/>，
/// 返回所有通道的并集（去重）。
/// </summary>
public sealed class CompositeChannelEnumerator : IChannelEnumerator
{
    private readonly IChannelEnumerator[] _enumerators;

    public CompositeChannelEnumerator(IEnumerable<IChannelEnumerator> enumerators)
    {
        _enumerators = (enumerators ?? throw new ArgumentNullException(nameof(enumerators)))
            .Where(e => e.GetType() != typeof(CompositeChannelEnumerator))
            .ToArray();
    }

    public IReadOnlyList<ChannelInfo> Enumerate()
    {
        var seen = new HashSet<ushort>();
        var result = new List<ChannelInfo>();
        foreach (var enumerator in _enumerators)
        {
            foreach (var channel in enumerator.Enumerate())
            {
                if (seen.Add(channel.Handle))
                    result.Add(channel);
            }
        }
        return result;
    }
}