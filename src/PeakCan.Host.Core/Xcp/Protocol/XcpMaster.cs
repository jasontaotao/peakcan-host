using System.Collections.Concurrent;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Xcp.Abstractions;

namespace PeakCan.Host.Core.Xcp.Protocol;

/// <summary>
/// XCP 单发单收会话引擎（spec §1 无 INTERLEAVED ⇒ 单发单收；spec §3 Protocol 超时策略）。
/// <para>
/// 复用 <see cref="XcpCommandEncoder"/> 产生的 <see cref="XcpCtoFrame"/> 请求与
/// <see cref="XcpResponseDecoder"/> 的错误码语义：本层只做 pending-response 配对、
/// T1 超时、重试与归因，正响应原样上抛（PID 0xFF + 载荷），由调用侧按命令解码。
/// </para>
/// <para>
/// 帧分流：正响应 PID 0xFF / 错误 PID 0xFE 之外的帧（DAQ DTO 0x00–0xFB、
/// EV 0xFD、SERV 0xFC）一律消费并忽略——EV 帧不算协议错误（T2 评审决策 a）；
/// 无 pending 时的正响应（迟到/错位帧）丢弃让超时语义接管（对齐 UdsClient C-8 先例）。
/// </para>
/// <para>
/// Thread-safety: 单请求互斥——pending 期间第二个请求直接拒绝（不排队，写死
/// 于 T4：排队会拖慢 DAQ 调度，调用侧应自行串行化命令序列）。
/// </para>
/// <para>
/// 残余风险（T4 评审裁决 (1) 的跨命令变体）：重试用尽抛出 XcpTimeoutException
/// 后，命令 A 的迟到正响应仍可能到达并被误配给其后的新命令 B——XCP 无命令
/// 关联 ID，本层 pending 存在即配对（见 OnFrameReceived）。调用侧（归因层
/// T11+）在超时后应保持 quiesce 间隙（≥ T1）再发起下一条命令。
/// </para>
/// </summary>
public sealed class XcpMaster : IDisposable
{
    private readonly IXcpTransport _transport;
    private readonly XcpMasterOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _requestLock = new(1, 1);

    // 与 UdsClient 同型的响应关联句柄：所有访问经 Volatile.Read/Write，
    // 保证 FrameReceived 回调线程（transport 分发循环）观察到最新值。
    private TaskCompletionSource<byte[]>? _responseTcs;

    public XcpMaster(IXcpTransport transport, XcpMasterOptions options, TimeProvider? timeProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _transport.FrameReceived += OnFrameReceived;
    }

    /// <summary>
    /// 发送一条 XCP 命令并等待响应（正响应返回完整帧字节，含 PID 0xFF）。
    /// </summary>
    /// <exception cref="XcpErrorResponseException">从机负响应（不重试，直接上抛；
    /// ERR_CMD_SYNCH 亦不自动重发 SYNCH，恢复动作由归因层决定——T2 评审决策 b）。</exception>
    /// <exception cref="XcpTimeoutException">T1 超时且重试用尽（归因查 FramesDropped）。</exception>
    /// <exception cref="InvalidOperationException">pending 期间并发第二请求，或底层写帧失败。</exception>
    /// <exception cref="OperationCanceledException">调用方取消。</exception>
    public async Task<byte[]> SendAsync(XcpCtoFrame command, CancellationToken ct = default)
    {
        // 单发单收：try-acquire 失败即拒绝。XCP 无命令回显，乱序并发请求
        // 无法与响应配对，宁可快速失败也不静默串扰。
        if (!await _requestLock.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException(
                "XcpMaster is single-request: a previous command is still pending (no INTERLEAVED support, spec §1).");

        try
        {
            return await SendWithRetryAsync(command, ct).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task<byte[]> SendWithRetryAsync(XcpCtoFrame command, CancellationToken ct)
    {
        // 归因基线：首次尝试前快照 FramesDropped。超时后 delta>0 说明
        // 采集 DTO 洪泛造成本机丢帧，而非从机无响应（T2 评审决策 c）。
        var framesDroppedAtStart = _transport.FramesDropped;
        var attempts = _options.MaxRetries + 1;
        var requestFrame = new CanFrame(
            _options.MasterCanId,
            command.Bytes,
            FrameFlags.None,
            ChannelId.None,
            default);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            Volatile.Write(ref _responseTcs, new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously));
            // T1 超时由 TimeProvider 驱动的定时器触发（测试用 FakeTimeProvider
            // 虚拟时钟推进）；回调只取消 pending TCS。
            using var timer = _timeProvider.CreateTimer(
                static state => ((XcpMaster)state!).CancelPendingResponse(), this, _options.Timeout, Timeout.InfiniteTimeSpan);
            // Caller cancellation while awaiting the response must throw OCE
            // immediately instead of being deferred to the T1 timer.
            using var ctRegistration = ct.Register(static (state, token) =>
                Volatile.Read(ref ((XcpMaster)state!)._responseTcs)?.TrySetCanceled(token), this);

            try
            {
                var result = await _transport.WriteAsync(requestFrame, ct).ConfigureAwait(false);
                if (!result.IsSuccess)
                    throw new InvalidOperationException($"XCP command write failed: {result.Error?.Message ?? "unknown error"}");

                return await _responseTcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // T1 超时：中止 pending，进入下一次重试（若有）。
            }
            finally
            {
                // 先摘定时器/取消注册再清 TCS，避免 dispose 竞态（UdsClient 严格排序先例）。
                timer.Dispose();
                ctRegistration.Dispose();
                Volatile.Write(ref _responseTcs, null);
            }
        }

        var delta = _transport.FramesDropped - framesDroppedAtStart;
        throw new XcpTimeoutException(_options.Timeout, attempts, delta);
    }

    private void CancelPendingResponse() => Volatile.Read(ref _responseTcs)?.TrySetCanceled();

    private void OnFrameReceived(CanFrame frame)
    {
        if (frame.Data.Length < 1)
            return;

        var tcs = Volatile.Read(ref _responseTcs);
        var pid = frame.Data.Span[0];

        if (pid == XcpPid.PositiveResponse)
        {
            // XCP 正响应无命令回显：pending 存在即配对，否则视为迟到帧丢弃。
            tcs?.TrySetResult(frame.Data.ToArray());
            return;
        }

        if (pid == XcpPid.Error && tcs is not null)
        {
            var bytes = frame.Data.ToArray();
            try
            {
                tcs.TrySetException(new XcpErrorResponseException(XcpResponseDecoder.Error(bytes)));
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
            {
                // 解码失败（帧过短/未知错误码）立即以异常终结请求，
                // 而不是让分发循环吞掉异常后挂到超时。
                tcs.TrySetException(new InvalidOperationException("Malformed XCP error response.", ex));
            }
        }

        // 其余 PID（DAQ DTO 0x00–0xFB、EV 0xFD、SERV 0xFC）：消费并忽略。
    }

    public void Dispose()
    {
        _transport.FrameReceived -= OnFrameReceived;
        _requestLock.Dispose();
    }
}

/// <summary>XCP 命令超时（T1 × 重试用尽），携带断流归因。</summary>
public sealed class XcpTimeoutException : TimeoutException
{
    /// <summary>单次尝试的超时时长（T1）。</summary>
    public TimeSpan Timeout { get; }

    /// <summary>实际尝试次数（1 + MaxRetries）。</summary>
    public int Attempts { get; }

    /// <summary>首次尝试到最终超时之间 transport 的 FramesDropped 增量。</summary>
    public long FramesDroppedDelta { get; }

    /// <summary>断流归因。</summary>
    public XcpTimeoutCause Attribution { get; }

    public XcpTimeoutException(TimeSpan timeout, int attempts, long framesDroppedDelta)
        : base($"XCP command timed out after {attempts} attempt(s) of {timeout.TotalMilliseconds:F0} ms " +
               $"(frames dropped during wait: {framesDroppedDelta}).")
    {
        Timeout = timeout;
        Attempts = attempts;
        FramesDroppedDelta = framesDroppedDelta;
        Attribution = framesDroppedDelta > 0
            ? XcpTimeoutCause.LocalFrameDrop
            : XcpTimeoutCause.SlaveNoResponse;
    }
}

/// <summary>XCP 负响应（PID 0xFE + 错误码）。</summary>
public sealed class XcpErrorResponseException : Exception
{
    public XcpErrorResponse Response { get; }

    public XcpErrorResponseException(XcpErrorResponse response)
        : base($"XCP negative response: {response.Code} (0x{(byte)response.Code:X2}).")
    {
        Response = response;
    }
}

/// <summary>
/// 超时断流归因：查 transport 的 FramesDropped 增量区分本机丢帧
/// （DTO 洪泛挤占，采集层应降载）与从机无响应（链路/从机故障）。
/// </summary>
public enum XcpTimeoutCause
{
    /// <summary>等待期间无本机丢帧：从机对命令无响应。</summary>
    SlaveNoResponse,

    /// <summary>等待期间 FramesDropped 增长：本机丢帧（DTO 洪泛挤占），应答帧通道本身不丢帧。</summary>
    LocalFrameDrop,
}
