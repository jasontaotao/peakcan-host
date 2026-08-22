using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Multi-channel assertion context. Holds a dictionary of SingleChannelContext
/// keyed by logical channel name. Routes IAssertionContext/IHasFrameSink calls
/// by channelName: null/empty → default channel; specific name → matching channel.
///
/// Sink fan-out：SetFrameSink(null, sink) 将所有通道的帧写入同一 sink（合并 .asc）；
/// SetFrameSink("bus-a", sink) 只挂载到 bus-a。
/// </summary>
internal sealed class MultiChannelAssertionContext : IAssertionContext, IHasFrameSink, IHasRecentFrames, IStepVariableStore, IDisposable
{
    private readonly IReadOnlyDictionary<string, SingleChannelContext> _channels;
    private readonly string _defaultChannelName;

    /// <summary>默认通道名（第一个注册的，或由 ctor 指定）。</summary>
    public string DefaultChannelName => _defaultChannelName;

    /// <summary>所有通道名列表。</summary>
    public IEnumerable<string> ChannelNames => _channels.Keys;

    /// <summary>通道数。</summary>
    public int ChannelCount => _channels.Count;

    /// <param name="channels">通道字典（key = 逻辑通道名，value = SingleChannelContext）。</param>
    /// <param name="defaultChannelName">默认通道名。null 或用第一个注册的通道。</param>
    /// <exception cref="ArgumentException">channels 为空或 defaultChannelName 不在字典中。</exception>
    public MultiChannelAssertionContext(
        IReadOnlyDictionary<string, SingleChannelContext> channels,
        string? defaultChannelName = null)
    {
        if (channels is null || channels.Count == 0)
            throw new ArgumentException("Must provide at least one channel.", nameof(channels));

        _channels = channels;

        // 默认通道：如果未指定或用第一个
        _defaultChannelName = defaultChannelName ?? channels.Keys.First();
        if (!_channels.ContainsKey(_defaultChannelName))
            throw new ArgumentException(
                $"Default channel name '{defaultChannelName}' not found in channels dictionary.",
                nameof(defaultChannelName));
    }

    // ── IAssertionContext ──

    /// <summary>按通道名订阅解码帧流。null = 默认通道。</summary>
    public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame)
        => ResolveChannel(null).SubscribeDecodedFrames(onFrame);

    /// <summary>按通道名订阅（显式实现 override DIM 默认）。channelName null/空 = 默认通道。</summary>
    public IDisposable SubscribeDecodedFrames(string? channelName, Action<DecodedFrame> onFrame)
        => ResolveChannel(channelName).SubscribeDecodedFrames(onFrame);

    public double? GetSignalValue(string signalName, int maxAgeMs = 5000)
        => ResolveChannel(null).GetSignalValue(signalName, maxAgeMs);

    public double CurrentTimestamp => ResolveChannel(null).CurrentTimestamp;

    /// <summary>按通道名发送帧。channelName null/空 = 默认通道。</summary>
    public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct)
        => ResolveChannel(null).SendFrameAsync(frame, ct);

    /// <summary>按通道名发送（显式实现 override DIM 默认）。channelName null/空 = 默认通道。</summary>
    public ValueTask<Result<Unit>> SendFrameAsync(string? channelName, CanFrame frame, CancellationToken ct)
    {
        try
        {
            return ResolveChannel(channelName).SendFrameAsync(channelName, frame, ct);
        }
        catch (KeyNotFoundException)
        {
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.NotFound,
                $"Channel '{channelName}' not found. Available: {string.Join(", ", _channels.Keys)}"));
        }
    }

    public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames()
        => ResolveChannel(null).GetRecentDecodedFrames();

    /// <summary>按通道名查最近解码帧。</summary>
    public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames(string? channelName)
        => ResolveChannel(channelName).GetRecentDecodedFrames(channelName);

    // ── IHasFrameSink ──

    /// <summary>挂载 sink 到默认通道。</summary>
    public void SetFrameSink(IHilFrameSink? sink)
        => SetFrameSink(null, sink);

    /// <summary>
    /// 按通道名挂载/摘除 sink。
    /// channelName null/空 → 挂载到所有通道（fan-out，合并 .asc）。
    /// channelName 非空 → 只挂载到指定通道。
    /// </summary>
    public void SetFrameSink(string? channelName, IHilFrameSink? sink)
    {
        if (string.IsNullOrEmpty(channelName))
        {
            // Fan-out: mount sink on all channels (merged .asc)
            foreach (var ctx in _channels.Values)
                ctx.SetFrameSink(sink);
        }
        else
        {
            // Mount on specific channel
            if (_channels.TryGetValue(channelName, out var ctx))
                ctx.SetFrameSink(sink);
        }
    }

    public async Task WaitForFrameDrainAsync(CancellationToken ct = default)
    {
        // 排空所有通道
        foreach (var ctx in _channels.Values)
            await ctx.WaitForFrameDrainAsync(ct).ConfigureAwait(false);
    }

    // ── IHasRecentFrames ──

    public IReadOnlyList<CanFrame> GetRecentFrames()
        => ResolveChannel(null).GetRecentFrames();

    // ── IStepVariableStore ──

    /// <summary>共享步骤变量（跨所有通道）。</summary>
    public IDictionary<string, object> Variables { get; } = new Dictionary<string, object>();

    // ── ResolveChannelId ──

    /// <summary>
    /// 将逻辑通道名映射到底层 ICanChannel 的物理 ChannelId。
    /// Task 9 的 executor 需要此方法：ctx.ResolveChannelId(p.TargetChannel) 构造 CanFrame 的 Channel。
    /// </summary>
    /// <param name="channelName">逻辑通道名。null/空 = 默认通道。</param>
    /// <returns>对应的 ChannelId；未知通道名返回 ChannelId.None。</returns>
    public ChannelId ResolveChannelId(string? channelName)
    {
        var ctx = ResolveChannel(channelName, allowMissing: true);
        return ctx?.ChannelId ?? ChannelId.None;
    }

    // ── IDisposable ──

    public void Dispose()
    {
        foreach (var ctx in _channels.Values)
            ctx.Dispose();
    }

    // ── Internal ──

    /// <summary>
    /// 解析 channelName 到对应的 SingleChannelContext。
    /// null/空 → 默认通道。
    /// 未知通道名 → 抛出（或 allowMissing 时返回 null）。
    /// </summary>
    private SingleChannelContext ResolveChannel(string? channelName, bool allowMissing = false)
    {
        if (string.IsNullOrEmpty(channelName))
            channelName = _defaultChannelName;

        if (_channels.TryGetValue(channelName, out var ctx))
            return ctx;

        if (allowMissing)
            return null!;

        throw new KeyNotFoundException(
            $"Channel name '{channelName}' not found. Available: {string.Join(", ", _channels.Keys)}");
    }
}