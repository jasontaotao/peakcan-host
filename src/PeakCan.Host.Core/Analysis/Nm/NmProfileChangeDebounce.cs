namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// profile 文件变化事件去抖（spec §10.1.2「≥200ms」）。
/// <para>
/// <b>不是滑动窗口</b>：判定基于「上次<em>接受</em>」而不是「上次<em>收到</em>」。
/// 滑窗会让持续刷新的目录永远不触发——每 50ms 一个事件时，任何时刻距上次收到
/// 都不到 200ms，失效永远不发。
/// </para>
/// <para>
/// 时间由调用方传入而不是读 <c>Environment.TickCount</c>：这样测试可以精确驱动，
/// 不靠 <c>Thread.Sleep</c>。
/// </para>
/// </summary>
public sealed class NmProfileChangeDebounce
{
    private readonly TimeSpan _window;
    private TimeSpan? _lastAccepted;

    public NmProfileChangeDebounce(TimeSpan window)
    {
        if (window < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), "去抖窗口不能为负");
        _window = window;
    }

    /// <summary>
    /// 在 <paramref name="now"/> 收到一个事件：是否应当向上抛出（触发失效）。
    /// <para>
    /// <b>这里只判定「是否在窗口内」，不推进窗口</b>。推进必须由调用方在
    /// <b>真正抛出之后</b>调 <see cref="MarkRaised"/>。
    /// </para>
    /// <para>
    /// 原因：调用方在 ShouldRaise 之后还有「文件是否正在被写」的检查，可能
    /// 决定跳过本次。若在 ShouldRaise 里就推进窗口，那次跳过会白白消耗掉窗口，
    /// 紧随其后的真实事件被吞，整次修改都不会触发失效。
    /// </para>
    /// </summary>
    public bool ShouldRaise(TimeSpan now)
        => _lastAccepted is not { } last || now - last >= _window;

    /// <summary>记录「这次真的抛出了」。只在 <see cref="ShouldRaise"/> 返回 true 且确实触发后调用。</summary>
    public void MarkRaised(TimeSpan now) => _lastAccepted = now;
}
