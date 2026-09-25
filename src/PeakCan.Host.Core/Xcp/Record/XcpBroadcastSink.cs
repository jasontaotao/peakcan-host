using PeakCan.Host.Core.Xcp.Receive;

namespace PeakCan.Host.Core.Xcp.Record;

/// <summary>
/// S4-T1（spec D4）：广播 sink——S3 会话单 sink 注入（XcpReceiveOptions.Sink）下，
/// 卡片管线（XcpCardPanelSink）与 MDF 记录 sink 共存的组合形态。
/// <para>
/// 队列纪律（S2 IXcpAcquisitionSink 契约）：本类<b>无队列、纯顺序转发</b>，入队不阻塞由
/// 各子 sink 自行承担（卡片 sink 有界队列 DropOldest、记录 sink 有界队列 + 后台写线程）。
/// </para>
/// <para>
/// 异常隔离（spec D4 定案）：单个子 sink 的 OnValues/OnGap 抛异常<b>被本层吸收</b>，
/// 不向上传播、不拖其他子 sink——记录 sink 故障不毒化卡片管线（D5 故障隔离的广播层前提）。
/// 计数出站 <see cref="ErrorCount"/> 保证"丢弃必须可见"（对齐 S2 LocalFrameDrop 归因精神）；
/// 子 sink 自身的故障面（如记录写盘异常 → 状态区红字）由子 sink 各自的观测口上报，
/// 不经广播层代传。注意：吸收意味着上游 CallbackFailed 归因不会因子 sink 异常触发——
/// 这是隔离的代价，spec D4 如实转述（S2 契约的 OnGap 异常本就无归因去向）。
/// </para>
/// <para>
/// IDisposable：组合根 singleton 生命周期由容器管理；本类不拥有子 sink，
/// Dispose 只为将来加资源时留形状（当前无状态，测试用 using 表达所有权意图）。
/// </para>
/// </summary>
public sealed class XcpBroadcastSink : IXcpAcquisitionSink, IDisposable
{
    private readonly IXcpAcquisitionSink[] _sinks;
    private long _errorCount;

    /// <param name="sinks">子 sink 集合（顺序即广播顺序）；null 元素拒绝（配置错误 fail-fast）。</param>
    public XcpBroadcastSink(IEnumerable<IXcpAcquisitionSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = [.. sinks];
        if (_sinks.Any(s => s is null))
            throw new ArgumentNullException(nameof(sinks), "null child sink");
    }

    /// <summary>子 sink 异常累计计数（values 与 gaps 同计数；丢弃必须可见）。</summary>
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    /// <summary>顺序广播样本；单子异常吸收并计数，不阻断后续子 sink。</summary>
    public void OnValues(XcpDaqSample sample)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                sink.OnValues(sample);
            }
            catch
            {
                Interlocked.Increment(ref _errorCount);
            }
        }
    }

    /// <summary>顺序广播归因条目；异常语义同 <see cref="OnValues"/>。</summary>
    public void OnGap(XcpAcquisitionGap gap)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                sink.OnGap(gap);
            }
            catch
            {
                Interlocked.Increment(ref _errorCount);
            }
        }
    }

    public void Dispose()
    {
        // 无状态纯转发：显式空实现留形状（见类注释），不拥有子 sink。
    }
}
