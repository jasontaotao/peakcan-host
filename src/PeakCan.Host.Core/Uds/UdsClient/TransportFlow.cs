using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Core.Uds.IsoTp;

namespace PeakCan.Host.Core.Uds;

public partial class UdsClient
{
    // Flow A: Transport wire layer + Rx + Dispose + test seam.
    // SendRequestAsync -> SendRequestInternalAsync -> OnMessageReceived + OnP2TimeoutFired.
    // Extracted from UdsClient.cs verbatim per W12 D5 (test-seam + Dispose grouped
    // with the OnMessageReceived subscription handler they pair with).
    //
    // Cross-flow callers (partial-class visible):
    //   - SendRequestAsync <-- all 13 UDS service methods (Flow B/C/D/E)

    /// <summary>
    /// Send a UDS service request and wait for response.
    /// </summary>
    /// <param name="serviceId">Service ID (SID).</param>
    /// <param name="data">Service data (excluding SID).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Response bytes (excluding SID + 0x40).</returns>
    /// <remarks>
    /// v1.2.14 PATCH Item 4: marked <c>virtual</c> so test doubles can
    /// intercept wire-level frame emit without subclassing the entire
    /// <see cref="UdsClient"/>. Visibility stays <c>public</c> for
    /// backwards compatibility with existing direct callers
    /// (e.g. <c>UdsClientTests</c>).
    /// </remarks>
    public virtual async Task<byte[]> SendRequestAsync(byte serviceId, byte[]? data = null, CancellationToken ct = default)
    {
        // Build request: SID + data
        byte[] request;
        if (data is null)
        {
            request = [serviceId];
        }
        else
        {
            request = new byte[1 + data.Length];
            request[0] = serviceId;
            Array.Copy(data, 0, request, 1, data.Length);
        }

        // Serialize requests to prevent overlapping
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SendRequestInternalAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>
    /// 断连复位：把「上一台 ECU 留下的」诊断状态整体清掉。这是**唯一**的重连复位
    /// 入口——调用方（<c>SessionPanelViewModel.ResetForConnectionChange</c>）只调这一个
    /// 方法，不要自己拼 <c>Session.Reset()</c> + <c>Transport.Reset()</c>。
    /// <para>
    /// <b>为什么必须集中在一处：</b>真正生效的超时在 <see cref="_timer"/>
    /// （<c>SendRequestInternalAsync</c> 与 NRC 0x78 路径的 <c>CancelAfter</c> 读它），
    /// 而 <see cref="Session"/> 不持有它。只复位 Session 会造出「面板显示 50/5000、
    /// 实际仍在用上一台 ECU 的协商值」——比不复位更糟，因为界面在说谎。
    /// </para>
    /// <para>
    /// <b>顺序与仲裁：</b>先用 <c>_requestLock.Wait(0)</c> 判定有没有在途请求。
    /// <list type="bullet">
    /// <item>拿得到 → 没有在途请求 → 在**持锁期间**复位 ISO-TP 与会话/时序状态。</item>
    /// <item>拿不到 → 有在途请求 → 取消它（让等待方立刻收到超时）但**跳过 ISO-TP 复位**。
    /// 此时清 <c>_txWaitingForFc</c> 会让正在等 Flow Control 的多帧发送把「不在等待」
    /// 误判成「FC 到了」，继续往 ECU 灌 CF。</item>
    /// </list>
    /// 判定必须发生在取消之前：取消后请求会异步解开并释放锁，先取消再判定会得到
    /// 不确定的结果。
    /// </para>
    /// <para>
    /// <b>为什么状态复位也要在持锁分支内：</b>否则在 <c>Release()</c> 与
    /// <c>_timer.ResetToDefaults()</c> 之间，另一线程的新请求可以拿到锁、用**旧的**
    /// P2 执行 <c>CancelAfter</c>，随后才被复位。
    /// </para>
    /// <para>
    /// <b>仍然存在的窗口：</b>一条 <c>0x10</c> 的续写（<c>Session.SetSession</c> +
    /// 写 <c>_timer</c>）发生在 <c>SendRequestAsync</c> 返回之后、不持锁。本方法
    /// 通过自增 <see cref="ResetEpoch"/> 让那条续写自己放弃写入——见
    /// <c>SessionFlow.DiagnosticSessionControlAsync</c> 的比对。
    /// </para>
    /// <para>
    /// 基线取 <see cref="UdsTimer"/> 的默认值。阶段 4 引入用户配置后，这里改传
    /// 配置值即可，<see cref="UdsSession.Reset"/> 本身不用动。
    /// </para>
    /// </summary>
    public virtual void ResetDiagnosticState()
    {
        // 先递增代际号：从这一刻起，任何已越过 SendRequestAsync 但尚未写状态的
        // 0x10 续写都会比对失败并放弃。必须在任何状态改动之前发生。
        Interlocked.Increment(ref _resetEpoch);

        // 保活循环必须先停：否则复位后它还会继续向旧 ECU 发 3E 00。
        Session.StopS3KeepAlive();

        var lockHeld = false;
        try
        {
            lockHeld = _requestLock.Wait(0);
        }
        catch (ObjectDisposedException)
        {
            // 客户端已 Dispose（窗口关闭/进程退出与断连竞态）。此时没有活着的
            // 传输要保护，也不再持有可用的锁——直接走状态复位即可。
            lockHeld = false;
        }

        if (lockHeld)
        {
            try
            {
                _isoTp.Reset();
                ResetNegotiatedState();
            }
            catch (ObjectDisposedException)
            {
                // _isoTp 已 Dispose（与 Dispose 竞态）。状态复位本身不依赖
                // ISO-TP，照常执行。
                ResetNegotiatedState();
            }
            finally
            {
                try { _requestLock.Release(); }
                catch (ObjectDisposedException) { /* 与 Dispose 竞态，锁已不存在 */ }
            }
            return;
        }

        // 有在途请求：尽力取消，让等待方收到超时而不是继续等满 P2。
        // 不 Dispose——由 SendRequestInternalAsync 的 finally 负责释放。
        // 读两次以缩小「两次 Volatile.Write 之间读到 null」的窗口（见
        // SendRequestInternalAsync 的发布顺序）。
        try
        {
            (Volatile.Read(ref _responseCts) ?? Volatile.Read(ref _responseCts))?.Cancel();
        }
        catch (ObjectDisposedException) { /* 请求已在收尾，无需处理 */ }

        ResetNegotiatedState();
    }

    /// <summary>
    /// 应用新的 CAN ID 配置，带仲裁：有在途 UDS 请求时**拒绝**而不是排队。
    /// <para>
    /// 用 <see cref="_requestLock"/> 而非 <c>IsoTpLayer._sendGate</c> 做门控：前者同时
    /// 保证「没有在途 UDS 请求」，比后者（只保护多帧发送）更严格，且与
    /// <c>SendRequestAsync</c> 用同一把锁，不引入第二套并发模型。
    /// </para>
    /// <para>
    /// 拿不到锁说明有请求在跑——此时换 ID 会让「请求已发到旧地址、响应在新地址上
    /// 等」永久失配。返回 <c>false</c> 让 UI 提示「稍后再试」，不要阻塞 UI 线程。
    /// </para>
    /// </summary>
    /// <param name="config">新配置。</param>
    /// <param name="error">失败原因（成功时为 null）。</param>
    /// <returns>是否已应用。</returns>
    public bool TryApplyTransportConfig(CanIdConfig config, out string? error)
    {
        ArgumentNullException.ThrowIfNull(config);
        error = null;

        bool lockHeld;
        try
        {
            lockHeld = _requestLock.Wait(0);
        }
        catch (ObjectDisposedException)
        {
            error = "诊断客户端已释放，无法修改通讯参数。";
            return false;
        }

        if (!lockHeld)
        {
            error = "当前有诊断请求正在进行，请等请求结束后再修改通讯参数。";
            return false;
        }

        try
        {
            // 同步重配：这里持有 _requestLock，必须同步完成。IsoTpLayer 提供了
            // 同步的 TryReconfigure，避免 sync-over-async 在 UI 线程上冻结。
            _isoTp.TryReconfigure(config);
            // 换了寻址，上一台 ECU 的会话/时序/安全状态全部作废。
            ResetNegotiatedState();
            return true;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            error = $"CAN ID 超出所选帧格式范围：{ex.Message}";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { _requestLock.Release(); }
            catch (ObjectDisposedException) { /* 与 Dispose 竞态 */ }
        }
    }

    /// <summary>
    /// 设置用户配置的时序基线，并立即生效。
    /// <para>
    /// <b>必须同时写 <see cref="_timer"/> 与 <see cref="Session"/>。</b>真正决定请求
    /// 超时的是 <c>_timer</c>（<c>SendRequestInternalAsync</c> 的 <c>CancelAfter</c>
    /// 与 NRC 0x78 路径都读它），<see cref="Session"/> 只负责界面展示。只写后者会让
    /// 界面显示 2000 ms、实际仍按 50 ms 超时——「显示与行为不符」，比不改更糟。
    /// </para>
    /// <para>
    /// 记下来的基线还会被 <see cref="ResetNegotiatedState"/> 复用：断连复位与
    /// CAN ID 重配都回到这里，而不是硬编码的 50/5000。
    /// </para>
    /// </summary>
    /// <param name="p2Ms">P2 基线（毫秒），必须 &gt; 0。</param>
    /// <param name="p2StarMs">P2* 基线（毫秒），必须 &gt; 0。</param>
    /// <param name="hasUserOverride">true = 来自用户配置，界面标注「用户配置」。</param>
    /// <exception cref="ArgumentOutOfRangeException">任一值 ≤ 0。</exception>
    public void ApplyTimingBaseline(int p2Ms, int p2StarMs, bool hasUserOverride)
    {
        if (p2Ms <= 0)
            throw new ArgumentOutOfRangeException(nameof(p2Ms), p2Ms, "P2 must be positive (ms).");
        if (p2StarMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(p2StarMs), p2StarMs, "P2* must be positive (ms).");

        _userTimingBaseline = hasUserOverride ? (p2Ms, p2StarMs) : null;

        _timer.P2Timeout = TimeSpan.FromMilliseconds(p2Ms);
        _timer.P2StarTimeout = TimeSpan.FromMilliseconds(p2StarMs);
        // 只改时序，不动 SessionType：用户改超时值时 ECU 可能还停在编程会话里，
        // 本地模型不能因此宣称「默认会话」。
        Session.SetTimingBaseline(p2Ms, p2StarMs, hasUserOverride);
    }

    /// <summary>
    /// 复位会话/时序/安全这三处「上一台 ECU 的状态」。
    /// <para>
    /// 真正生效的超时在 <see cref="_timer"/>，展示在 <see cref="Session"/>——
    /// 两者必须一起复位，只改一边就是「界面说谎」。
    /// </para>
    /// <para>
    /// 时序回到**用户基线**（若配过）而不是内置默认：用户配了 2000 ms，断连重连
    /// 后仍然该是 2000 ms。
    /// </para>
    /// </summary>
    private void ResetNegotiatedState()
    {
        if (_userTimingBaseline is { } baseline)
        {
            _timer.P2Timeout = TimeSpan.FromMilliseconds(baseline.P2Ms);
            _timer.P2StarTimeout = TimeSpan.FromMilliseconds(baseline.P2StarMs);
            Session.Reset(baseline.P2Ms, baseline.P2StarMs, hasUserOverride: true);
        }
        else
        {
            _timer.ResetToDefaults();
            Session.Reset(
                (int)UdsTimer.DefaultP2Timeout.TotalMilliseconds,
                (int)UdsTimer.DefaultP2StarTimeout.TotalMilliseconds);
        }

        // 安全访问是 ECU 侧状态，断连后一律按「未解锁」处理。
        // UdsSecurity.Reset 刻意保留 lockout 计数（见其 XML 注释）。
        Security.Reset();
    }

    public void Dispose()
    {
        _isoTp.MessageReceived -= OnMessageReceived;
        _requestLock.Dispose();
        Volatile.Read(ref _responseCts)?.Dispose();
        Session.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<byte[]> SendRequestInternalAsync(byte[] request, CancellationToken ct)
    {
        _pendingRequestSid = request[0];
        Volatile.Write(ref _responseTcs, new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously));
        Volatile.Write(ref _responseCts, CancellationTokenSource.CreateLinkedTokenSource(ct));

        // v1.2.13 PATCH Item 4: register a callback so P2 timeout unblocks
        // await _responseTcs.Task. Without this registration the linked CTS
        // cancel only fires OperationCanceledException for whoever awaits the
        // token directly - and nothing does. P2 timeout would silently hang
        // the caller. The callback TrySetCancels the TCS so the await resumes
        // with TaskCanceledException -> caught below -> rethrown as UdsException.
        var registration = _responseCts.Token.Register(
            static state => ((UdsClient)state!).OnP2TimeoutFired(), this);

        // Register timeout
        _responseCts.CancelAfter(_timer.P2Timeout);

        try
        {
            // Send via ISO-TP
            await _isoTp.SendMessageAsync(request, ct).ConfigureAwait(false);

            // Wait for response
            var response = await _responseTcs.Task.ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UdsException("UDS response timeout");
        }
        finally
        {
            // v1.2.13 PATCH Item 4 (Phase 2.5 new finding): strict ordering
            // matters here. Disposal sequence:
            //   1. registration.Dispose()  - unhook the Token.Register callback
            //                                so it cannot fire during/after Dispose
            //   2. Volatile.Write(_responseTcs, null) - OnMessageReceived sees no TCS
            //   3. Volatile.Write(_responseCts, null) - OnMessageReceived sees no CTS
            //   4. cts?.Dispose()           - last; no in-flight reference remains
            // Without this ordering OnMessageReceived may cts?.CancelAfter on a
            // disposed CTS (ObjectDisposedException propagates onto the SDK
            // read thread - process crash on graceful shutdown).
            registration.Dispose();
            var cts = Volatile.Read(ref _responseCts);
            Volatile.Write(ref _responseTcs, null);
            Volatile.Write(ref _responseCts, null);
            cts?.Dispose();
        }
    }

    private void OnP2TimeoutFired()
    {
        OnP2TimeoutFiredForTesting?.Invoke();
        Volatile.Read(ref _responseTcs)?.TrySetCanceled();
    }

    private void OnMessageReceived(byte[] data)
    {
        if (data.Length < 1)
            return;

        byte sid = data[0];

        // Item 14: acquire-load the pending response handles. Without
        // Volatile.Read the JIT may have cached or hoisted the read.
        var tcs = Volatile.Read(ref _responseTcs);
        var cts = Volatile.Read(ref _responseCts);

        // Check for negative response (0x7F)
        if (sid == 0x7F && data.Length >= 3)
        {
            byte requestedSid = data[1];
            byte nrc = data[2];

            // Handle NRC 0x78 (requestCorrectlyReceivedResponsePending)
            if (nrc == 0x78)
            {
                // v1.2.13 PATCH Item 4 (Phase 2.5): guard against disposed
                // CTS. After SendRequestInternalAsync's finally has nulled
                // the fields and disposed cts, a late-arriving response
                // (already in flight on the SDK read thread) would crash
                // here. The IsCancellationRequested check is the cheap
                // fast-path; the try/catch handles the disposed-after-read
                // race window.
                if (cts is not null && !cts.IsCancellationRequested)
                {
                    var armed = _timer.P2StarTimeout;
                    LastP2StarArmedForTesting = armed;
                    try { cts.CancelAfter(armed); }
                    catch (ObjectDisposedException) { /* shutdown race */ }
                }
                return;
            }

            // Other NRCs: complete with error
            tcs?.TrySetException(new UdsNegativeResponseException(requestedSid, (UdsNegativeResponseCode)nrc));
            return;
        }

        // Positive response: SID + 0x40
        if (data.Length >= 2)
        {
            // C-8 fix: validate the SID echoes our in-flight request's SID + 0x40.
            // A misaligned SID means the frame is stale or from a different
            // request; dropping it lets the P2 timer expire (semantically correct).
            byte expectedPositiveSid = (byte)(_pendingRequestSid + 0x40);
            if (sid != expectedPositiveSid)
                return;

            tcs?.TrySetResult(data[1..]);
            // Runs on the SDK read thread inside the window the reset epoch guards.
            OnResponseDeliveredForTesting?.Invoke();
        }
    }

    /// <summary>
    /// v1.2.13 PATCH Item 4: test-only public surface for OnMessageReceived.
    /// Allows tests to drive late-arriving-response paths without standing
    /// up an IsoTpLayer + ICanChannel.
    /// </summary>
    internal void PublicOnMessageReceivedForTesting(byte[] data) => OnMessageReceived(data);

    /// <summary>
    /// Test-only hook fired synchronously right after a positive response completes the
    /// in-flight TCS, i.e. before the awaiting continuation resumes.
    /// <para>
    /// The TCS is created with <c>RunContinuationsAsynchronously</c>, so the continuation
    /// is queued to the thread pool rather than run inline — this hook therefore runs in
    /// the exact window between "response delivered" and "caller's post-await code writes
    /// session/timer state". That window has no lock protecting it (the request lock is
    /// still held by the request thread, and the continuation writes after releasing it),
    /// which is why <see cref="ResetEpoch"/> exists.
    /// </para>
    /// <para>Production code never sets this.</para>
    /// </summary>
    internal Action? OnResponseDeliveredForTesting { get; set; }
}
