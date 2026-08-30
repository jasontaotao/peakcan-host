namespace PeakCan.HIL.Core.J1939;

public sealed partial class J1939TpLayer
{
    /// <summary>RTS/CTS 发送会话。</summary>
    internal sealed class RtsCtsTxSession
    {
        public required uint Pgn { get; init; }
        public required byte Priority { get; init; }
        public required byte TargetDa { get; init; }
        public required int TotalBytes { get; init; }
        public required int TotalPackets { get; init; }

        /// <summary>当前等待阶段的 waiter（由 <see cref="RegisterWaiter"/> 注册、HandleTxControl 消费）。</summary>
        public TaskCompletionSource<TpCmMessage>? Waiter;

        /// <summary>无 waiter 窗口内先到的控制帧（如对端续授权 CTS/EOM_ACK 恰在两次注册间抵达）。
        /// FIFO，注册下一 waiter 时补交付（防丢唤醒）；会话移除时随之丢弃。</summary>
        public readonly Queue<TpCmMessage> PendingControls = new();
    }

    /// <summary>
    /// RTS/CTS 点对点发送（spec §6.2）。Task 在收到 EOM_ACK（校验一致）后完成；
    /// CTS 包数=0 → hold（T4 内等下一 CTS）；ConnAbort/超时 → Error。
    /// 同一 (SA, DA) 同时只允许一个发送状态机（J1939 限制），重入立即 Error。
    /// </summary>
    public async Task<Result<Unit>> SendRtsCtsAsync(
        uint pgn, byte priority, byte sa, byte da, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (!TryValidatePayload(payload.Length, out var validationError))
            return Result<Unit>.Fail(ErrorCode.InvalidArgument, validationError);

        var key = new SessionKey(sa, da);
        var session = new RtsCtsTxSession
        {
            Pgn = pgn,
            Priority = priority,
            TargetDa = da,
            TotalBytes = payload.Length,
            TotalPackets = (payload.Length + 6) / 7,
        };
        lock (_gate)
        {
            if (_txSessions.ContainsKey(key))
                return Result<Unit>.Fail(ErrorCode.InvalidState, $"到 SA 0x{sa:X2}/DA 0x{da:X2} 的 RTS/CTS 发送正在进行中");
            _txSessions[key] = session;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            int nextPacket = 1;

            // 修订 P1（有据，见 task-7-report）：waiter 必须先于 RTS 上线注册（防丢唤醒，计划修订的绑定决策）
            // ——对端 CTS 可在 _sendAsync(rtsFrame) 调用内同步直达 HandleTxControl，若彼时 waiter 未注册，
            // 该帧只能靠 PendingControls 暂存兜底；先注册则同步直达即可交付。这也是
            // RtsCtsTxSession.Waiter 字段注释的本意（brief 原稿在 RTS 发完后的循环内才注册，实测挂死）。
            var waiter = RegisterWaiter(session);

            var rtsFrame = new CanFrame(
                new CanId(J1939Id.Compose(priority, TpCmPgn, sa, da), FrameFormat.Extended),
                TpCmMessage.Rts((ushort)payload.Length, (byte)session.TotalPackets, _options.RtsMaxPacketsPerCts, pgn).Encode(),
                FrameFlags.None, ChannelId.None, default);
            var rtsResult = await _sendAsync(rtsFrame, ct).ConfigureAwait(false);
            if (!rtsResult.IsSuccess)
                return FailFrom(rtsResult);

            while (true)
            {
                // 注：TpCmMessage 是 struct——brief 原稿对 TpCmMessage? 直接成员访问（cts.Control 等）
                // 无法编译（CS1061，实测；Nullable&lt;T&gt; 不提升属性访问）。先判空、解包为非可空局部变量，
                // 其后代码与 brief 逐字一致。
                var ctsOrTimeout = await AwaitControl(waiter, _options.T3Ms, timeoutCts).ConfigureAwait(false);
                if (ctsOrTimeout is null)
                    return ct.IsCancellationRequested
                        ? Result<Unit>.Fail(ErrorCode.Cancelled, "发送已取消")
                        : Result<Unit>.Fail(ErrorCode.InvalidState, "等待 CTS 超时（T3）");
                TpCmMessage cts = ctsOrTimeout.Value;

                if (cts.Control == TpCmControl.ConnAbort)
                    return Result<Unit>.Fail(ErrorCode.InvalidState, $"对端中止（原因 {cts.AbortReason}）");
                if (cts.Control == TpCmControl.EomAck)
                    break;   // 循环外处理（不应在等待 CTS 阶段收到，防御性中断）

                // CTS：包数 0 = hold → T4 内等下一 CTS；否则发段
                byte grant = cts.MaxPacketsPerCts;
                byte from = cts.NextPacketNumber == 0 ? (byte)1 : cts.NextPacketNumber;
                if (grant == 0)
                {
                    var holdWaiter = RegisterWaiter(session);
                    var nextOrTimeout = await AwaitControl(holdWaiter, _options.T4Ms, timeoutCts).ConfigureAwait(false);
                    if (nextOrTimeout is null)
                        return ct.IsCancellationRequested
                            ? Result<Unit>.Fail(ErrorCode.Cancelled, "发送已取消")
                            : Result<Unit>.Fail(ErrorCode.InvalidState, "CTS hold 等待超时（T4）");
                    TpCmMessage next = nextOrTimeout.Value;
                    if (next.Control == TpCmControl.ConnAbort)
                        return Result<Unit>.Fail(ErrorCode.InvalidState, $"对端中止（原因 {next.AbortReason}）");
                    grant = next.MaxPacketsPerCts;
                    from = next.NextPacketNumber == 0 ? (byte)1 : next.NextPacketNumber;
                }

                // 修订 P1（续）：waiter 须先于 DT 段上线注册（防丢唤醒）——续授权 CTS（grant < 剩余）与
                // EOM_ACK 都可经发送回调在 DT 段发送期间同步内联到达；brief 原稿在段发完后才注册
                // eomWaiter/循环顶 waiter → 唤醒丢失。此处一个 waiter 同时服务"等下一 CTS"（续授权，
                // 循环顶 T3）与"等 EOM_ACK"（段发完，T3）两个后续等待阶段；窗口内先到的帧由
                // PendingControls 暂存、注册时补交付。
                waiter = RegisterWaiter(session);

                var dtId = new CanId(J1939Id.Compose(priority, TpDtPgn, sa, da), FrameFormat.Extended);
                for (int i = 0; i < grant && from + i <= session.TotalPackets; i++)
                {
                    int seq = from + i;
                    int take = Math.Min(7, payload.Length - (seq - 1) * 7);
                    var chunk = new byte[take];
                    payload.Span.Slice((seq - 1) * 7, take).CopyTo(chunk);
                    var dtFrame = new CanFrame(dtId, new TpDtMessage((byte)seq, chunk).Encode(), FrameFlags.None, ChannelId.None, default);
                    var dtResult = await _sendAsync(dtFrame, ct).ConfigureAwait(false);
                    if (!dtResult.IsSuccess)
                        return FailFrom(dtResult);
                }
                nextPacket = from + grant;

                var eomOrTimeout = nextPacket > session.TotalPackets
                    ? await AwaitControl(waiter, _options.T3Ms, timeoutCts).ConfigureAwait(false)
                    : null;
                if (eomOrTimeout is not null)
                {
                    TpCmMessage eom = eomOrTimeout.Value;
                    if (eom.Control == TpCmControl.ConnAbort)
                        return Result<Unit>.Fail(ErrorCode.InvalidState, $"对端中止（原因 {eom.AbortReason}）");
                    if (eom.Control == TpCmControl.EomAck)
                    {
                        if (eom.TotalSize != session.TotalBytes || eom.TotalPackets != session.TotalPackets)
                            return Result<Unit>.Fail(ErrorCode.InvalidState, $"EOM_ACK 总长/包数不一致（{eom.TotalSize}/{eom.TotalPackets}）");
                        // 注：brief 原稿写 Ok(Unit.Value)，包内 Unit 是空结构体、无 Value 成员
                        //（CS0117，实测；Task 5/6 同款修订 + 全仓 20+ 处既有先例均为 Result<Unit>.Ok(default)）。
                        return Result<Unit>.Ok(default);
                    }

                    // 修订 P1（配套）：EOM 阶段收到非 EOM_ACK/Abort 控制帧（如对端错发的 CTS）时，原 waiter
                    // 已被本次 AwaitControl 消费；若直接落回循环顶对同一（已完成的）waiter 再等待，会把同一
                    // 控制帧再消费一次、按其授权重复发段。重新注册新 waiter 再落回循环顶，行为对齐 brief
                    // （其循环顶每次注册全新 waiter，静默等下一帧/T3 超时）。
                    waiter = RegisterWaiter(session);
                }
            }

            return Result<Unit>.Fail(ErrorCode.InvalidState, "等待 EOM_ACK 阶段收到非预期控制帧");
        }
        catch (OperationCanceledException)
        {
            // 修订 C1（有据，见 task-7-report / Task 5 同款）：取消时 AwaitControl 路径按约定返回
            // Result(ErrorCode.Cancelled)（上方 ct.IsCancellationRequested 分支），但 _sendAsync(frame, ct)
            // 若被取消抛出 OperationCanceledException 会沿 async Task 逃逸、`await send` 得到异常而非
            // Result——与本方法"超时/取消 → Error"的 Result 契约矛盾。整包捕获 OCE → Fail(Cancelled)。
            return Result<Unit>.Fail(ErrorCode.Cancelled, "发送已取消");
        }
        finally
        {
            lock (_gate)
                _txSessions.Remove(key);
        }
    }

    /// <summary>
    /// 注册下一等待阶段的 waiter。若注册前已有控制帧先到（存于 <see cref="RtsCtsTxSession.PendingControls"/>），
    /// 按 FIFO 以队首在同一临界区内立即消费并完成该 waiter（防丢唤醒）。
    /// <para>修订 F1（review fix）：安装 waiter、消费队首、完成 waiter 三步必须在同一 _gate 临界区内
    /// 原子完成——原实现把 TrySetResult 放在锁外，读线程可趁"锁已放、尚未完成"的窗口取走刚安装的
    /// waiter 先行完成，队首帧 TrySetResult 失败且已被出队 → 静默丢失。锁内 TrySetResult 是安全的：
    /// RunContinuationsAsynchronously 使续接恒排队线程池、绝不内联执行状态机代码，故持锁期间不会有
    /// 任何会再取 _gate 的代码运行；且全部 completer（本方法与 HandleTxControl）自此都串行于 _gate，
    /// TrySetResult 恒成功、无帧可失。</para>
    /// </summary>
    private TaskCompletionSource<TpCmMessage> RegisterWaiter(RtsCtsTxSession session)
    {
        var waiter = new TaskCompletionSource<TpCmMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            session.Waiter = waiter;
            if (session.PendingControls.Count > 0)
            {
                session.Waiter = null;   // 队首帧在本临界区内即时消费
                waiter.TrySetResult(session.PendingControls.Dequeue());
            }
        }
        return waiter;
    }

    /// <summary>等待控制帧或超时（T3/T4 竞速；watchdog 只管接收侧 T1，见修订 14）。</summary>
    private async Task<TpCmMessage?> AwaitControl(
        TaskCompletionSource<TpCmMessage> waiter, int timeoutMs, CancellationTokenSource timeoutCts)
    {
        // 注：brief 原稿写 Task.Delay(timeoutMs, _timeProvider, timeoutCts.Token)，但 Task.Delay 无
        // (int, TimeProvider, ct) 重载（CS1503，实测；Task 5 同款修订）→ TimeSpan.FromMilliseconds 包装。
        var delay = Task.Delay(TimeSpan.FromMilliseconds(timeoutMs), _timeProvider, timeoutCts.Token);
        var finished = await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);
        if (finished == waiter.Task)
            return waiter.Task.Result;

        if (delay.IsCanceled)
            return null;   // 外层 ct 取消 → 调用方按 Cancelled 归类
        return null;       // 超时
    }

    /// <summary>
    /// PF=0xEC 的 CTS/EOM_ACK/Abort 路由到发送状态机（方向反转：控制帧 SA=对端、DA=本机）。
    /// 修订 P2（有据，见 task-7-report）：无 waiter 窗口（状态机正在处理上一帧/尚未注册下一 waiter，
    /// 续接为线程池排队、与帧到达线程天然并发）内到达的控制帧不丢弃，按 FIFO 暂存于
    /// <see cref="RtsCtsTxSession.PendingControls"/>，下一次注册时补交付——否则脚本化/跨线程喂帧下
    /// 下一帧被静默丢弃、状态机在 FakeTimeProvider 下永挂（实测：Hold_Cts_Zero_Then_Continue 与
    /// Rts_Declares_Sender_MaxPackets_Per_Cts 逐测试运行均挂死）。真实链路上对端 CTS/EOM_ACK 相对
    /// 本端阶段切换本就异步到达，暂存兜底同样是正确的产线行为。
    /// <para>修订 F1（review fix）：取 waiter、置 null、TrySetResult 三步移入同一 _gate 临界区原子完成，
    /// 堵住与 <see cref="RegisterWaiter"/> 早交付之间的 lost-frame 竞态窗口（任一方在锁外完成，另一方的
    /// 帧即被静默丢弃）。锁内 TrySetResult 的安全性论证见 <see cref="RegisterWaiter"/>。</para>
    /// </summary>
    private partial void HandleTxControl(J1939Id id, TpCmMessage cm)
    {
        lock (_gate)
        {
            // 发送会话键 (Sa=本机, Da=对端)；控制帧 (Sa=对端, Da=本机) → 查 (PS, SA)
            var key = new SessionKey(id.PduSpecific, id.SourceAddress);
            if (!_txSessions.TryGetValue(key, out var session) || session.Pgn != cm.Pgn)
                return;
            var waiter = session.Waiter;
            if (waiter is null)
            {
                session.PendingControls.Enqueue(cm);   // 无 waiter 窗口先到 → 暂存，注册时补交付
                return;
            }
            session.Waiter = null;
            waiter.TrySetResult(cm);   // F1：锁内原子完成（续接恒线程池排队，不内联重入状态机）
        }
    }
}
