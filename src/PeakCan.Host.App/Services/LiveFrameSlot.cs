namespace PeakCan.Host.App.Services;

/// <summary>
/// 单槽缓存：只保存当前选中消息的最新一帧载荷。
/// <para>
/// 后台解码 worker 在 <see cref="Publish"/> 时比对目标消息 ID，非选中消息直接丢弃，
/// 因此总线上有多少帧都不会产生额外拷贝。UI 的 33ms 节流器通过
/// <see cref="TryRead"/> 取最新一帧；<see cref="LiveFramePayload.Sequence"/> 用于
/// 跳过没有新帧的 tick。
/// </para>
/// <para>
/// 线程安全：worker 线程写、UI 定时器线程读。锁内只做不可变快照的引用交换；
/// 载荷数组发布后不再修改。
/// </para>
/// </summary>
public sealed class LiveFrameSlot
{
    private const int NoTarget = -1;

    private readonly object _gate = new();

    // uint 消息 ID 转 int 存储；无目标用 -1。
    private int _targetId = NoTarget;

    private LiveFramePayload? _latest;
    private long _sequence;

    /// <summary>
    /// 设置当前选中消息；null 表示没有选中消息。切换到不同消息时清空旧载荷，
    /// 防止上一条消息的帧点亮新选中的消息。
    /// </summary>
    public void SetTarget(uint? messageId)
    {
        var next = messageId is { } id ? unchecked((int)id) : NoTarget;
        lock (_gate)
        {
            if (next != _targetId) _latest = null;
            _targetId = next;
        }
    }

    /// <summary>
    /// 后台 worker 调用：缓存匹配目标消息的最新载荷。不匹配则直接返回，不分配。
    /// </summary>
    public void Publish(uint messageId, ReadOnlySpan<byte> data)
    {
        if (unchecked((int)messageId) != Volatile.Read(ref _targetId)) return;

        var copy = data.ToArray();
        lock (_gate)
        {
            // 拷贝期间目标可能被切走，锁内再确认一次。
            if (unchecked((int)messageId) != _targetId) return;
            _latest = new LiveFramePayload(copy, ++_sequence);
        }
    }

    /// <summary>
    /// 取目标消息的最新载荷；目标不匹配或还没有帧时返回 false。
    /// 传 <paramref name="messageId"/> 而不是只读槽，是为了让迟到的 tick 无法把
    /// 上一条消息的载荷应用到新选中消息上。
    /// </summary>
    public bool TryRead(uint messageId, out LiveFramePayload payload)
    {
        lock (_gate)
        {
            if (_targetId == unchecked((int)messageId) && _latest is { } latest)
            {
                payload = latest;
                return true;
            }
        }

        payload = default;
        return false;
    }

    /// <summary>丢弃缓存；DBC 重载时调用，避免旧文档的帧点亮新文档同 ID 消息。</summary>
    public void Clear()
    {
        lock (_gate) { _latest = null; }
    }
}

/// <summary>一帧载荷快照；<see cref="Data"/> 发布后不再修改。</summary>
public readonly record struct LiveFramePayload(ReadOnlyMemory<byte> Data, long Sequence);
