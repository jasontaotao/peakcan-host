using System.Collections;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

/// <summary>
/// 线程安全的帧收集器，作为测试里 <c>new List&lt;CanFrame&gt;()</c> 的 drop-in 替代。
/// 后台周期定时器线程经 <c>FakeChannel.OnWrite</c> 写入，测试线程并发枚举/断言——
/// 直接用 <see cref="List{T}"/> 会偶发 "Collection was modified; enumeration operation
/// may not execute"（并行负载下放大，见 EnvironmentRuleDispatchTests flake）。
/// Add/Count/this[int]/枚举（快照）语义与 List 兼容。
/// </summary>
internal sealed class SentList : IEnumerable<CanFrame>
{
    private readonly List<CanFrame> _items = new();

    public void Add(CanFrame frame)
    {
        lock (_items) _items.Add(frame);
    }

    public int Count
    {
        get { lock (_items) return _items.Count; }
    }

    public CanFrame this[int index]
    {
        get { lock (_items) return _items[index]; }
    }

    public IEnumerator<CanFrame> GetEnumerator()
    {
        List<CanFrame> snapshot;
        lock (_items) snapshot = new List<CanFrame>(_items);
        return snapshot.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
