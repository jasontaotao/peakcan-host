using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.HIL.Core.Services;

namespace PeakCan.Host.App.Services.J1939;

/// <summary>
/// J1939 周期发送规格（VM 解析后的中间形态）。
/// <para>承载周期 tick 所需的全部解析产物：目标层、单帧发送委托（单帧模式路由）、
/// 以及 Compose 参数（PGN / 优先级 / SA / 可空 DA / 载荷 / 模式）。</para>
/// <para>Task 19 修订（有据）：brief 原稿 internal——但 J1939SendViewModel 为 public、
/// 其 public ctor 以 <see cref="J1939CyclicSendService"/> 为参数，internal 类型出现在
/// public 成员签名触发 CS0051（实测）；Services/J1939 既有类（J1939ReassemblyService 等）
/// 均为 public。随 CS0051 一并转 public，语义不变。</para>
/// </summary>
public sealed record J1939SendSpec(
    J1939TpLayer Layer,
    Func<CanFrame, CancellationToken, ValueTask<Result<Unit>>> SingleFrameSend,
    uint Pgn, byte Priority, byte Sa, byte? Da,
    byte[] Payload,
    TpMode Mode);

/// <summary>
/// 周期 J1939 发送（镜像 CyclicDbcSendService 对 ITimerFactory 的用法；定时器不进 Core，spec §8.2）。
/// <para>
/// 形如 <c>ICyclicSendService</c>（IsRunning / SuccessCount / FailureCount / Start / Stop），
/// 但 Start 的载荷是 <see cref="J1939SendSpec"/>（J1939 发送按模式分流，非单帧形状），故不实现该接口。
/// </para>
/// <para>
/// <b>并发契约</b>：J1939TpLayer 的 SendBamAsync 并发约束（同 (PGN, SA) 串行）由调用方保证——
/// tick 侧以 <c>_inFlight</c> 闸实现：上一轮（BAM 7 包 / RTS-CTS 会话）未完成时跳过本轮，
/// 绝不在途重入。
/// </para>
/// <para>Task 19 修订（有据）：brief 原稿 internal——J1939SendViewModel（public）的
/// public ctor 以本类型为参数，CS0051 强制转 public（同 J1939SendSpec，实测）。
/// </para>
/// </summary>
public sealed class J1939CyclicSendService : IDisposable
{
    private readonly ITimerFactory _timerFactory;
    private readonly ILogger<J1939CyclicSendService> _logger;
    private readonly object _gate = new();
    private ICyclicTimer? _timer;
    private J1939SendSpec? _spec;
    private int _inFlight;
    // 修订（有据，task-19）：brief 原稿 SuccessCount/FailureCount 为自动属性 +
    // OnTick 里 Interlocked.Increment(ref SuccessCount)——CS0206（属性不可作 ref 实参），
    // 实测无法编译。按 CyclicDbcSendService 先例改私有字段 + Interlocked.Read 只读属性，
    // 计数语义不变。
    private long _successCount;
    private long _failureCount;

    /// <summary>True when the cyclic send timer is active.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Sends the channel/layer reported as successful since service construction.</summary>
    public long SuccessCount => Interlocked.Read(ref _successCount);

    /// <summary>Sends reported as failed (Result error or thrown) since service construction.</summary>
    public long FailureCount => Interlocked.Read(ref _failureCount);

    /// <summary>Production ctor — timer factory + optional logger (DI resolves both; logger may be omitted in tests).</summary>
    public J1939CyclicSendService(ITimerFactory timerFactory, ILogger<J1939CyclicSendService>? logger = null)
    {
        _timerFactory = timerFactory ?? throw new ArgumentNullException(nameof(timerFactory));
        _logger = logger ?? NullLogger<J1939CyclicSendService>.Instance;
    }

    /// <summary>
    /// Start periodic transmission of <paramref name="spec"/> at
    /// <paramref name="interval"/>. If already running, the previous timer is
    /// disposed first (mirror of CyclicDbcSendService.Start re-entry).
    /// </summary>
    public void Start(J1939SendSpec spec, TimeSpan interval)
    {
        lock (_gate)
        {
            _spec = spec;
            _timer?.Dispose();
            _timer = _timerFactory.CreateCyclicTimer(OnTick, null, interval);
            IsRunning = true;
        }
    }

    /// <summary>Stop cyclic transmission. Idempotent.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _spec = null;
            IsRunning = false;
        }
    }

    private async void OnTick(object? state)
    {
        var spec = _spec;
        if (spec is null || Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            return;   // 上一轮未完成（BAM 7 包在途）→ 跳过本轮

        try
        {
            var result = spec.Mode switch
            {
                TpMode.Bam => await spec.Layer.SendBamAsync(spec.Pgn, spec.Priority, spec.Sa, spec.Payload).ConfigureAwait(false),
                TpMode.RtsCts when spec.Da is { } da => await spec.Layer.SendRtsCtsAsync(spec.Pgn, spec.Priority, spec.Sa, da, spec.Payload).ConfigureAwait(false),
                _ => await spec.SingleFrameSend(
                        new CanFrame(
                            new CanId(J1939Id.Compose(spec.Priority, spec.Pgn, spec.Sa, spec.Da ?? 0xFF), FrameFormat.Extended),
                            spec.Payload, FrameFlags.None, ChannelId.None, default),
                        CancellationToken.None),
            };

            if (result.IsSuccess) Interlocked.Increment(ref _successCount);
            else Interlocked.Increment(ref _failureCount);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failureCount);
            _logger.LogWarning(ex, "J1939 cyclic send failed");
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    /// <summary>Stop the timer (host shutdown / owner dispose). Idempotent via <see cref="Stop"/>.</summary>
    public void Dispose() => Stop();
}
