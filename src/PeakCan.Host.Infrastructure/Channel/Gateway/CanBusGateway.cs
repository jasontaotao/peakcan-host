using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Gateway;

namespace PeakCan.Host.Infrastructure.Channel.Gateway;

/// <summary>
/// 总线间帧转发网关：订阅 source.FrameReceived，按 GatewayConfig 过滤/映射后写入 target.WriteAsync。
/// 双向时对称订阅。防回环用"最近转发指纹 + 时间窗去重"。
/// </summary>
public sealed class CanBusGateway : IAsyncDisposable
{
    private const int AntiLoopbackWindowMs = 100;
    private readonly ICanChannel _source;
    private readonly ICanChannel _target;
    private readonly GatewayConfig _config;
    private readonly ILogger<CanBusGateway>? _logger;

    // H4: 最近转发指纹集合。双向网关的 OnSourceFrame/OnTargetFrame 分别在 source/target
    //     读循环线程执行（两线程并发），所有访问必须持 _recentLock。
    private readonly List<(uint Id, int Hash, DateTime Timestamp)> _recent = new();
    private readonly object _recentLock = new();
    private bool _started;

    // T3: 构造只做 null 校验 + 存依赖（无副作用）；Start() 显式启动订阅 —— 测试可构造后控制订阅时机。
    public CanBusGateway(ICanChannel source, ICanChannel target, GatewayConfig config,
        ILogger<CanBusGateway>? logger = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        // L2: 防御 loader 之外的直接构造路径 —— map 越界会在 Forward 的 new CanId 抛到读循环线程
        //     （FrameReceived 直接订阅非 per-subscriber 隔离，抛异常殃及其他订阅者/读循环）。
        if (config.MapToCanId is { } map && map > 0x1FFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(config), $"GatewayConfig.MapToCanId ({map}) exceeds 29-bit CAN ID limit (0x1FFFFFFF).");
        _logger = logger;
    }

    /// <summary>订阅 source（双向时也订阅 target）FrameReceived。幂等。</summary>
    public void Start()
    {
        if (_started) return;
        _source.FrameReceived += OnSourceFrame;
        if (_config.Bidirectional) _target.FrameReceived += OnTargetFrame;
        _started = true;
    }

    // M2: DisposeAsync 只退订事件，不 dispose source/target channel —— channel 生命周期归调用方。
    public ValueTask DisposeAsync()
    {
        if (_started)
        {
            _source.FrameReceived -= OnSourceFrame;
            _target.FrameReceived -= OnTargetFrame;
            _started = false;
        }
        return ValueTask.CompletedTask;
    }

    private void OnSourceFrame(CanFrame frame) => Forward(frame, _target);
    private void OnTargetFrame(CanFrame frame) => Forward(frame, _source);

    private void Forward(CanFrame frame, ICanChannel destination)
    {
        // 1. CAN-ID 范围过滤（原始 Id，含边界）
        if (_config.MinCanId is { } min && frame.Id.Raw < min) return;
        if (_config.MaxCanId is { } max && frame.Id.Raw > max) return;

        // 2. ID 映射（B1: 按 map 值自动选帧格式 —— map > 0x7FF 时目标必须是扩展帧）+ Channel 重写。
        var id = _config.MapToCanId is { } map
            ? new CanId(map, map > 0x7FF ? FrameFormat.Extended : FrameFormat.Standard)
            : frame.Id;
        var forwarded = frame with { Id = id, Channel = destination.Id };

        // 3. 防回环：用**转发帧**指纹去重（R1：映射后的 Id，回环中收到的帧 Id 与此一致 -> 命中）。
        if (!TryMarkRecent(forwarded)) return;

        // 4. H2: fire-and-forget async Task（非 async void）—— 异常在 WriteSafeAsync 内部捕获。
        _ = WriteSafeAsync(destination, forwarded);
    }

    // H3: 指纹 = (Id.Raw, HashCode(Data.Span, Flags))，不含 Channel/Timestamp（转发/读循环会变）。
    private bool TryMarkRecent(CanFrame frame)
    {
        var hash = DataHash(frame.Data.Span, frame.Flags);
        lock (_recentLock)
        {
            var cutoff = DateTime.UtcNow.AddMilliseconds(-AntiLoopbackWindowMs);
            _recent.RemoveAll(r => r.Timestamp < cutoff);
            foreach (var r in _recent)
                if (r.Id == frame.Id.Raw && r.Hash == hash)
                    return false;   // 窗口内重复 → 丢弃（防回环）
            _recent.Add((frame.Id.Raw, hash, DateTime.UtcNow));
            return true;
        }
    }

    private static int DataHash(ReadOnlySpan<byte> data, FrameFlags flags)
    {
        var hc = new HashCode();
        hc.AddBytes(data);
        hc.Add(flags);
        return hc.ToHashCode();
    }

    // H2: 转发写。async Task 方法（非 async void）—— 所有异常在方法内捕获。
    private async Task WriteSafeAsync(ICanChannel channel, CanFrame frame)
    {
        try
        {
            var result = await channel.WriteAsync(frame).ConfigureAwait(false);
            if (!result.IsSuccess)
                _logger?.LogWarning("Gateway forwarding failed: {Error}", result.Error?.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Gateway forwarding threw on {Channel}", channel.Id);
        }
    }
}
