using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Core.Uds.IsoTp;

public sealed partial class IsoTpLayer
{
    // Flow A: Lifecycle (v1.2.12 PATCH Item 2 + earlier).
    // 2 ctors + Reset + Dispose moved verbatim from IsoTpLayer.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - Reset -> CancelReceiveWatchdog (Flow E, cross-partial) + _rxLock/_rxInProgress/_rxBuffer/_txLock/_txWaitingForFc (state, main)
    //   - Dispose -> _sendGate (state, main)
    //   - ctors -> DI fields (main)

    /// <summary>Create a new ISO-TP layer.</summary>
    /// <param name="config">CAN ID configuration for request/response.</param>
    /// <param name="sendFrame">Callback to send a CAN frame.</param>
    /// <param name="txCanId">CAN ID for transmitted frames. Defaults to config.RequestId. For ECU simulators, set to config.ResponseId.</param>
    public IsoTpLayer(CanIdConfig config, Action<CanFrame> sendFrame, uint? txCanId = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(sendFrame);

        _runtime = new RuntimeConfig(config, txCanId ?? config.RequestId, txCanId is not null);
        _sendFrame = sendFrame;
    }

    /// <summary>
    /// v1.2.12 PATCH Item 2: async-callback ctor overload. The async callback
    /// is awaited internally so the SDK read thread never blocks on
    /// <c>.AsTask().Wait()</c> (the v1.2.11 deadlock root cause). Concurrent
    /// <see cref="SendMessageAsync"/> calls are serialized through a
    /// <see cref="SemaphoreSlim"/>(1,1) so the FF/CF sequence of one transport
    /// cannot interleave with another.
    /// </summary>
    /// <param name="config">CAN ID configuration for request/response.</param>
    /// <param name="sendFrame">Async callback to send a CAN frame.</param>
    /// <param name="logger">Optional logger for send-callback exceptions (logged at Error, not propagated).</param>
    /// <param name="txCanId">CAN ID for transmitted frames. Defaults to config.RequestId. For ECU simulators, set to config.ResponseId.</param>
    public IsoTpLayer(CanIdConfig config, Func<CanFrame, Task> sendFrame, ILogger<IsoTpLayer>? logger = null, uint? txCanId = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(sendFrame);

        _runtime = new RuntimeConfig(config, txCanId ?? config.RequestId, txCanId is not null);
        _sendFrameAsync = sendFrame;
        _logger = logger;
    }

    /// <summary>
    /// 运行时重配 CAN ID / 帧格式，立即生效（不重建实例、不重挂路由器）。
    /// <para>
    /// <b>为什么要走这个入口而不是直接改字段：</b><see cref="UdsClient"/> 持有
    /// 本层的引用且是 <c>readonly</c>，重建实例会让它指向一只已经无人投喂的层。
    /// 而所有读取点（接收过滤、发帧、功能寻址）都是实时读快照的，所以整体替换
    /// 快照即可立即生效。
    /// </para>
    /// <para>
    /// <b>仲裁：</b>先取 <c>_sendGate</c>，拿不到就抛
    /// <see cref="InvalidOperationException"/> 而不是排队——多帧传输跨多个 await，
    /// 中途换 ID 会让 FF 用旧 ID、后续 CF 用新 ID，ECU 丢掉整包。调用方应把它当作
    /// 「现在忙，稍后再试」，并提示用户。
    /// </para>
    /// <para>
    /// <b>校验先于应用：</b>用 <see cref="CanId"/> 构造函数自身的范围校验（标准帧
    /// ≤0x7FF、扩展帧 ≤0x1FFFFFFF）。任一 ID 越界就抛 <see cref="ArgumentOutOfRangeException"/>，
    /// 且不留下半应用的新配置。
    /// </para>
    /// <para>
    /// <b>会清掉传输协商态：</b>换了 ECU/寻址后，上一台 ECU 的 STmin/BS 与进行中的
    /// 重组都不再有意义（见 <see cref="Reset"/>）。
    /// </para>
    /// <para>
    /// <b>⚠️ 仲裁只覆盖多帧传输。</b>单帧 UDS 请求（如 0x22）不走 <c>_sendGate</c>，
    /// 所以本方法**无法**阻止「请求已按旧 ID 发出、响应按新 ResponseId 被过滤掉」。
    /// 生产代码请走 <c>UdsClient.TryApplyTransportConfig</c>——它额外用
    /// <c>_requestLock</c> 保证没有在途 UDS 请求。直接调用本方法仅适用于
    /// 「确定此刻无任何请求」的场景（如连接建立前）。
    /// </para>
    /// </summary>
    /// <param name="config">新配置。必须非 null。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">某个 CAN ID 超出所选帧格式的范围。</exception>
    /// <exception cref="InvalidOperationException">有多帧传输正在进行，拒绝重配。</exception>
    public Task ReconfigureAsync(CanIdConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        // 校验在前：CanId 构造函数会按帧格式校验范围。FunctionalId 可空。
        var format = config.IsExtendedFrame ? FrameFormat.Extended : FrameFormat.Standard;
        _ = new CanId(config.RequestId, format);
        _ = new CanId(config.ResponseId, format);
        if (config.FunctionalId is { } functionalId)
            _ = new CanId(functionalId, format);

        // 零超时取门：可用则同步成功，不可用则同步返回 false。两个分支都不真正
        // 异步，所以整个重配是同步完成的——这正是同步入口 TryReconfigure 能安全
        // 存在的前提。若将来把这里改成真正的等待，请同时改掉 TryReconfigure。
        if (!_sendGate.Wait(0, ct))
            throw new InvalidOperationException(
                "无法重配 CAN ID：多帧传输正在进行。请等当前传输结束后重试。");

        try
        {
            ApplyReconfigure(config);
        }
        finally
        {
            _sendGate.Release();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 同步版重配。存在的理由：<c>UdsClient.TryApplyTransportConfig</c> 在持有
    /// <c>_requestLock</c> 的状态下调用它，必须同步完成——若用
    /// <c>.GetAwaiter().GetResult()</c> 包异步版本，将来异步版本里一旦出现真正
    /// 的挂起，就会在 WPF UI 线程上冻结界面。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">某个 CAN ID 超出所选帧格式的范围。</exception>
    /// <exception cref="InvalidOperationException">有多帧传输正在进行，拒绝重配。</exception>
    internal void TryReconfigure(CanIdConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var format = config.IsExtendedFrame ? FrameFormat.Extended : FrameFormat.Standard;
        _ = new CanId(config.RequestId, format);
        _ = new CanId(config.ResponseId, format);
        if (config.FunctionalId is { } functionalId)
            _ = new CanId(functionalId, format);

        if (!_sendGate.Wait(0))
            throw new InvalidOperationException(
                "无法重配 CAN ID：多帧传输正在进行。请等当前传输结束后重试。");

        try
        {
            ApplyReconfigure(config);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>调用方必须已持有 <c>_sendGate</c>。</summary>
    private void ApplyReconfigure(CanIdConfig config)
    {
        var previous = Runtime;
        // 保留「发送 ID 是否被显式覆盖」的意图：ECU 模拟器（发送走 ResponseId）
        // 重配后仍应跟随新的 ResponseId，而不是被悄悄改成 RequestId。
        var txCanId = previous.TxCanIdOverridden ? config.ResponseId : config.RequestId;

        Volatile.Write(ref _runtime, new RuntimeConfig(config, txCanId, previous.TxCanIdOverridden));

        // 换寻址后，旧 ECU 的协商态与半截重组都必须作废。
        Reset();
    }

    /// <summary>
    /// 复位 ISO-TP 的收发状态。用于断连复位与运行时重配（<see cref="ReconfigureAsync"/>）。
    /// <para>
    /// <b>必须清掉整份状态，不只是「进行中」标志。</b>只清 <c>_rxInProgress</c> /
    /// <c>_txWaitingForFc</c> 会把上一台 ECU 的协商结果留在层内：
    /// <c>_hasReceivedFc</c> 一旦置 true 永不复位，通信参数面板于是把初始零值
    /// 标注成「ECU 给的 STmin/BS」——用户会相信这个标注。
    /// </para>
    /// <para>
    /// <b>调用方约束：</b>不得在有在途多帧发送时调用。清 <c>_txWaitingForFc</c>
    /// 会让 <c>WaitForFlowControlAsync</c> 把「不在等待」当成「FC 到了」返回 true，
    /// 正在等 FC 的传输于是继续往 ECU 灌 CF。仲裁由
    /// <c>UdsClient.ResetDiagnosticState</c> 负责（拿不到 <c>_requestLock</c> 就跳过）。
    /// </para>
    /// </summary>
    public void Reset()
    {
        lock (_rxLock)
        {
            _rxInProgress = false;
            _rxBuffer = null;
            _rxExpectedLength = 0;
            _rxReceivedLength = 0;
            _rxExpectedSequence = 1;
        }
        CancelReceiveWatchdog();

        lock (_txLock)
        {
            _txWaitingForFc = false;
            // FC 协商结果（BS / STmin）与「是否收到过 FC」属于对端 ECU 的状态，
            // 换 ECU 后必须失效。
            _txBlockSize = 0;
            _txStMin = 0;
            _hasReceivedFc = false;
        }

        // _cfCounter 在 SendMultiFrameAsync 的 _sendGate 内读写，而本方法**不取**
        // 那个 gate——真正保证无竞争的是上面的调用方契约（有在途多帧发送时不得
        // 调用 Reset），不是 gate 本身。_sendGate 只在 _cfCounter 的一个「纯计数
        // 归零」上做串行化并不值得（拿不到 gate 时该不该跳过归零没有正确答案）；
        // 目前生产调用点只有 UdsClient.ResetDiagnosticState，它用 _requestLock
        // 仲裁了这件事。
        _cfCounter = 0;
    }

    /// <summary>
    /// v1.2.12 PATCH Item 2: dispose the send-path semaphore so the layer
    /// can be replaced cleanly. The app owns a single IsoTpLayer per
    /// channel, so this is called at process shutdown.
    /// </summary>
    public void Dispose()
    {
        _sendGate.Dispose();
    }
}