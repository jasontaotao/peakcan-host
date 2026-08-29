using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.HIL.Core.J1939;

/// <summary>
/// SAE J1939-21 传输协议层（BAM + RTS/CTS，角色无关——spec §4）。
/// <para><b>线程模型</b>：<see cref="ProcessFrame"/> 运行于 SDK 读线程（经 sink adapter 分发）；
/// <see cref="MessageReceived"/>/<see cref="SessionEvent"/> 在调用 ProcessFrame 的线程同步引发，
/// 订阅方必须非阻塞，UI 更新自行 marshal。自动 CTS/EOM_ACK 采用"同步可完成则内联、否则线程池"的
/// fire-and-forget，绝不抛回读线程。</para>
/// </summary>
public sealed partial class J1939TpLayer
{
    /// <summary>TP.CM 的 PGN（60416）。</summary>
    public const uint TpCmPgn = 0x00EC00;

    /// <summary>TP.DT 的 PGN（60160）。</summary>
    public const uint TpDtPgn = 0x00EB00;

    /// <summary>会话归属键：DT 帧不含 PGN，按 (源地址, 目标地址) 归属；BAM 会话 Da=0xFF。</summary>
    internal readonly record struct SessionKey(byte Sa, byte Da);

    /// <summary>接收会话（BAM 与 RTS/CTS 接收方共用）。</summary>
    internal sealed class TpSession
    {
        public TpSession(int totalPackets) => Buffer = new byte[Math.Max(totalPackets, 1) * 7];

        public required uint Pgn { get; init; }
        public required byte Priority { get; init; }
        public required TpMode Mode { get; init; }
        public required int TotalBytes { get; init; }
        public required int TotalPackets { get; init; }
        public required double FirstFrameTimestampSec { get; init; }
        public int NextExpectedSeq = 1;
        public int ReceivedPackets;
        public bool GapDetected;
        public int GrantSinceCts;                 // 本次 CTS 授权后已收包数（RTS 接收方）
        public int CurrentGrant = int.MaxValue;   // 本次 CTS 授权的包数
        public byte[] Buffer;
        public double LastFrameTimestampSec;
    }

    private readonly Func<CanFrame, CancellationToken, ValueTask<Result<Unit>>> _sendAsync;
    private readonly J1939TpOptions _options;
    private readonly ILogger<J1939TpLayer>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<SessionKey, TpSession> _rxSessions = new();
    private readonly HashSet<byte> _localAddresses = new();
    private double _lastActivityTimestampSec;

    /// <summary>重组完成的消息（读线程同步引发，订阅方非阻塞）。</summary>
    public event Action<J1939Message>? MessageReceived;

    /// <summary>会话异常事件（读线程同步引发）。</summary>
    public event Action<J1939SessionEvent>? SessionEvent;

    public J1939TpLayer(
        Func<CanFrame, CancellationToken, ValueTask<Result<Unit>>> sendAsync,
        J1939TpOptions? options = null,
        ILogger<J1939TpLayer>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _sendAsync = sendAsync ?? throw new ArgumentNullException(nameof(sendAsync));
        _options = options ?? new J1939TpOptions();
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (!_options.OfflineMode)
            StartWatchdog();   // WatchdogFlow（Task 8 提供实现；此处先留 partial 调用）
    }

    /// <summary>注册本机身份（节点 Start 时调用；此后指向本机的 RTS 才会得到自动 CTS）。</summary>
    public void RegisterLocalAddress(byte sa) { lock (_gate) { _localAddresses.Add(sa); } }

    /// <summary>注销本机身份（节点 Stop 时调用；纯监听场景集合为空，层绝不注入 TP.CM）。</summary>
    public void UnregisterLocalAddress(byte sa) { lock (_gate) { _localAddresses.Remove(sa); } }

    /// <summary>
    /// 处理一帧 CAN 数据。非扩展帧、非 TP PGN（0xEB/0xEC）静默忽略；
    /// TP 数据畸形（长度不足/未知控制字节）抛 <see cref="ArgumentException"/>（由 sink adapter 窄捕获）。
    /// </summary>
    public void ProcessFrame(CanFrame frame) => ProcessFrameCore(frame);

    private partial void ProcessFrameCore(CanFrame frame);
    private partial void HandleTxControl(J1939Id id, TpCmMessage cm);   // Task 7 实现；本任务临时空实现
    private partial void StartWatchdog();   // Task 8 实现；本任务临时空实现（brief 未列此声明；无定义声明则 WatchdogFlow 占位实现报 CS0759，实测）

    /// <summary>锁外 + 逐 handler try/catch 的事件引发（同 IsoTpLayer v1.2.12 Item 3 模式）。</summary>
    private void RaiseMessageReceived(J1939Message message)
    {
        Action<J1939Message>? handlers;
        lock (_gate)
            handlers = MessageReceived;
        if (handlers is null)
            return;

        foreach (var d in handlers.GetInvocationList())
        {
            try
            {
                ((Action<J1939Message>)d)(message);
            }
            catch (Exception ex)
            {
                LogMessageHandlerFailed(_logger ?? NullLogger<J1939TpLayer>.Instance, ex, message.Payload.Length);
            }
        }
    }

    private void RaiseSessionEvent(J1939SessionEvent evt)
    {
        Action<J1939SessionEvent>? handlers;
        lock (_gate)
            handlers = SessionEvent;
        if (handlers is null)
            return;

        foreach (var d in handlers.GetInvocationList())
        {
            try
            {
                ((Action<J1939SessionEvent>)d)(evt);
            }
            catch (Exception ex)
            {
                LogMessageHandlerFailed(_logger ?? NullLogger<J1939TpLayer>.Instance, ex, 0);
            }
        }
    }

    /// <summary>fire-and-forget 发送：同步可完成则内联（测试确定性），否则线程池续跑；异常/失败仅记日志。</summary>
    private void FireAndForget(CanFrame frame)
    {
        ValueTask<Result<Unit>> pending;
        try
        {
            pending = _sendAsync(frame, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogSendFailed(_logger ?? NullLogger<J1939TpLayer>.Instance, ex, frame.Id.Raw);
            return;
        }

        if (pending.IsCompletedSuccessfully)
        {
            var result = pending.Result;
            if (!result.IsSuccess)
                LogSendFailed(_logger ?? NullLogger<J1939TpLayer>.Instance, new InvalidOperationException(result.Error?.Message ?? "send failed"), frame.Id.Raw);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await pending.ConfigureAwait(false);
                if (!result.IsSuccess)
                    LogSendFailed(_logger ?? NullLogger<J1939TpLayer>.Instance, new InvalidOperationException(result.Error?.Message ?? "send failed"), frame.Id.Raw);
            }
            catch (Exception ex)
            {
                LogSendFailed(_logger ?? NullLogger<J1939TpLayer>.Instance, ex, frame.Id.Raw);
            }
        });
    }
}
