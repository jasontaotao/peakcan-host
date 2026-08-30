using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.HIL.Core.J1939;

public sealed partial class J1939TpLayer
{
    /// <summary>PGN 的 PF 字节。</summary>
    private static byte PduFormatOf(uint pgn) => (byte)((pgn >> 8) & 0xFF);

    private static double ToSeconds(CanFrame frame) => frame.Timestamp.TotalMicroseconds / 1_000_000.0;

    private partial void ProcessFrameCore(CanFrame frame)
    {
        if (!frame.Id.IsExtended)
            return;

        var id = new J1939Id(frame.Id.Raw);
        if (id.PduFormat is not (0xEB or 0xEC))
            return;

        if (id.PduFormat == 0xEC)
        {
            var cm = TpCmMessage.Decode(frame.Data.Span);   // 畸形 → ArgumentException（契约）
            HandleControl(id, cm, frame);
        }
        else
        {
            var dt = TpDtMessage.Decode(frame.Data.Span);
            HandleDt(id, dt, frame);
        }
    }

    private void HandleControl(J1939Id id, TpCmMessage cm, CanFrame frame)
    {
        LogControlReceived(_logger ?? NullLogger<J1939TpLayer>.Instance, cm.Control.ToString());
        double ts = ToSeconds(frame);
        lock (_gate)
            _lastActivityTimestampSec = ts;

        switch (cm.Control)
        {
            case TpCmControl.Bam:
                CreateOrReplaceSession(new SessionKey(id.SourceAddress, 0xFF), id.Priority, cm, TpMode.Bam, ts);
                break;

            case TpCmControl.Rts:
                HandleRts(id, cm, frame, ts);
                break;

            case TpCmControl.Cts:
            case TpCmControl.EomAck:
            case TpCmControl.ConnAbort:
                HandleTxControl(id, cm);   // RtsCtsFlow（Task 7）；无发送会话时静默忽略
                break;
        }
    }

    // 注：brief 原稿此方法体内引用了不在作用域内的 `id.Priority`（无法编译），
    // 最小修订为显式传入 `byte priority`（两个调用点均传 id.Priority），其余逐字未动。
    // Task 4 review 修订：原稿在锁内记 3103 日志并引发 Superseded 事件，且 EvictIfFull_Locked
    // 在锁内记 3106 并引发 Evicted 事件——订阅者同步回调可能再进本层取 _gate 而自死锁；
    // 现改为锁内仅采集元数据（superseded/oversized/evicted），锁外记日志并引发事件
    // （与 HandleDt 的 lossEvent/completed 锁外引发同模式）。事件顺序不变：Evicted → Superseded。
    private void CreateOrReplaceSession(SessionKey key, byte priority, TpCmMessage cm, TpMode mode, double ts)
    {
        TpSession? superseded = null;
        bool oversized = false;
        (SessionKey Key, TpSession Victim)? evicted = null;
        lock (_gate)
        {
            if (_rxSessions.TryGetValue(key, out var existing))
            {
                superseded = existing;
                _rxSessions.Remove(key);
            }

            if (cm.TotalSize > _options.MaxPayloadBytes || cm.TotalPackets == 0)
            {
                oversized = true;   // 拒绝建会话；3103 日志与 Superseded 事件由锁外代码引发
            }
            else
            {
                evicted = EvictIfFull_Locked();
                _rxSessions[key] = new TpSession(cm.TotalPackets)
                {
                    Pgn = cm.Pgn,
                    Priority = priority,
                    Mode = mode,
                    TotalBytes = cm.TotalSize,
                    TotalPackets = cm.TotalPackets,
                    FirstFrameTimestampSec = ts,
                    LastFrameTimestampSec = ts,
                };
            }
        }

        if (oversized)
        {
            LogDeclaredLengthExceeds(_logger ?? NullLogger<J1939TpLayer>.Instance, cm.TotalSize, _options.MaxPayloadBytes);
            if (superseded is not null)
                RaiseSessionEvent(new J1939SessionEvent(SessionEventKind.Superseded, key.Sa, key.Da, superseded.Pgn, mode, "superseded by oversized declaration"));
            return;
        }

        if (evicted is not null)
        {
            LogSessionEvicted(_logger ?? NullLogger<J1939TpLayer>.Instance, _options.MaxConcurrentSessions);
            RaiseSessionEvent(new J1939SessionEvent(SessionEventKind.Evicted, evicted.Value.Key.Sa, evicted.Value.Key.Da, evicted.Value.Victim.Pgn, evicted.Value.Victim.Mode, "session table full"));
        }

        if (superseded is not null)
        {
            LogSessionSuperseded(_logger ?? NullLogger<J1939TpLayer>.Instance, key.Sa, key.Da, superseded.Pgn);
            RaiseSessionEvent(new J1939SessionEvent(SessionEventKind.Superseded, key.Sa, key.Da, superseded.Pgn, mode, "restarted"));
        }
    }

    private void HandleRts(J1939Id id, TpCmMessage cm, CanFrame frame, double ts)
    {
        bool isLocal;
        lock (_gate)
            isLocal = _options.AutoRespondToRts && id.PduSpecific != 0 && _localAddresses.Contains(id.PduSpecific);

        if (!isLocal)
            return;   // 纯监听：不建会话、不注入任何 TP.CM

        CreateOrReplaceSession(new SessionKey(id.SourceAddress, id.PduSpecific), id.Priority, cm, TpMode.RtsCts, ts);
        SendCts(id.SourceAddress, id.PduSpecific, id.Priority, cm);
    }

    /// <summary>接收方 CTS：grant = 策略放行包数（0=全部剩余，恒 ≥1）。</summary>
    private void SendCts(byte peerSa, byte localDa, byte priority, TpCmMessage rts)
    {
        int remaining = rts.TotalPackets;
        byte grant = _options.CtsMaxPackets == 0
            ? (byte)Math.Min(remaining, 0xFF)
            : (byte)Math.Min(_options.CtsMaxPackets, remaining);
        // Task 6 修订（初始授权记账，brief Files 列"补 GrantSinceCts 初始授权语义"）：初始 CTS
        // 放行的包数必须记入会话（CurrentGrant），否则 HandleDt 的授权耗尽判定永不触发、
        // CtsMaxPackets 分段策略下无续授权 CTS（RED 证据：brief 测试 Receiver_With_CtsMaxPackets_2_Segments_Grants）。
        // 记账须先于发送（FireAndForget 同步可完成时内联送达对端）；锁内仅改状态、锁外发送，
        // 与 Task 4 review 的锁纪律一致。会话不存在（超长 RTS 被拒）则无从记账，行为不变。
        lock (_gate)
        {
            if (_rxSessions.TryGetValue(new SessionKey(peerSa, localDa), out var s))
                s.CurrentGrant = grant;
        }

        var ctsFrame = new CanFrame(
            new CanId(J1939Id.Compose(priority, TpCmPgn, localDa, peerSa), FrameFormat.Extended),
            TpCmMessage.Cts(grant, 1, rts.Pgn).Encode(),
            FrameFlags.None,
            ChannelId.None,
            default);
        FireAndForget(ctsFrame);
    }

    private void HandleDt(J1939Id id, TpDtMessage dt, CanFrame frame)
    {
        double ts = ToSeconds(frame);
        var key = new SessionKey(id.SourceAddress, id.PduSpecific);
        TpSession? completed = null;
        J1939SessionEvent? lossEvent = null;
        CanFrame? ctsContinuation = null;   // Task 4 review：锁内仅构造帧，锁外 FireAndForget
        CanFrame? eomAck = null;            // （同步发送回调可能再进本层取 _gate 而自死锁）

        lock (_gate)
        {
            if (!_rxSessions.TryGetValue(key, out var s))
                return;   // 无会话 → 丢弃（总线上常见半截流量，不计错误）

            s.LastFrameTimestampSec = ts;
            if (dt.SequenceNumber == s.NextExpectedSeq)
            {
                StorePacket(s, dt.SequenceNumber, dt.Data.Span);
                s.ReceivedPackets++;
                s.NextExpectedSeq++;
            }
            else if (dt.SequenceNumber > s.NextExpectedSeq)
            {
                s.GapDetected = true;
                LogSequenceGap(_logger ?? NullLogger<J1939TpLayer>.Instance, s.NextExpectedSeq, dt.SequenceNumber);
                if (_options.OfflineMode)
                {
                    // 离线：保留会话继续收，flush 时按 PacketLoss 结算（spec 修订 6）
                    StorePacket(s, dt.SequenceNumber, dt.Data.Span);
                    s.ReceivedPackets++;
                    s.NextExpectedSeq = dt.SequenceNumber + 1;
                }
                else
                {
                    // 在线：会话作废 + PacketLoss 事件（spec §12）
                    _rxSessions.Remove(key);
                    lossEvent = new J1939SessionEvent(SessionEventKind.PacketLoss, key.Sa, key.Da, s.Pgn, s.Mode,
                        $"expected seq {s.NextExpectedSeq}, got {dt.SequenceNumber}");
                }
            }
            else
            {
                return;   // 旧/重复序号 → 忽略
            }

            if (s.ReceivedPackets == s.TotalPackets)
            {
                _rxSessions.Remove(key);
                completed = s;
            }
            else if (!_options.OfflineMode && s.GrantSinceCts >= s.CurrentGrant)
            {
                // RTS/CTS 接收方：本轮授权收满，授权剩余。
                // Task 6 修订（有据，见 task-6-report）：原稿 `GrantSinceCts + 1 >= CurrentGrant` 提前一包
                // 触发续授权——StorePacket 已先把 GrantSinceCts 累计到本包（授权 k 包收满第 k 包时
                // GrantSinceCts==k），`+1` 使判定在 k==CurrentGrant-1 即成立；brief 对打测试
                // Receiver_With_CtsMaxPackets_2_Segments_Grants 在 DT#2 后恰好断言 1 条续 CTS，
                // `>=` 才是"收满再补发"的语义。此前因 CurrentGrant 从未被初始 CTS 记账（恒
                // int.MaxValue）而潜伏，任何 CtsMaxPackets 分段策略下均不会暴露。
                s.GrantSinceCts = 0;
                s.CurrentGrant = _options.CtsMaxPackets == 0
                    ? Math.Min(s.TotalPackets - s.ReceivedPackets, 0xFF)
                    : Math.Min(_options.CtsMaxPackets, s.TotalPackets - s.ReceivedPackets);
                ctsContinuation = BuildCtsContinuation(key, s, (byte)s.CurrentGrant);
            }

            if (completed is not null && s.Mode == TpMode.RtsCts)
                eomAck = BuildEomAck(key, s);
        }

        // 锁外 fire-and-forget（保持原锁内发送相对事件引发的先后顺序：发送 → lossEvent → MessageReceived）。
        if (ctsContinuation is not null)
            FireAndForget(ctsContinuation.Value);
        if (eomAck is not null)
            FireAndForget(eomAck.Value);

        if (lossEvent is not null)
            RaiseSessionEvent(lossEvent);

        if (completed is not null)
        {
            var payload = new byte[completed.TotalBytes];
            Array.Copy(completed.Buffer, payload, Math.Min(completed.Buffer.Length, completed.TotalBytes));
            RaiseMessageReceived(new J1939Message(
                completed.Pgn, key.Sa, key.Da, completed.Priority, completed.Mode,
                payload, completed.FirstFrameTimestampSec, completed.LastFrameTimestampSec));
        }
    }

    private static void StorePacket(TpSession s, byte seq, ReadOnlySpan<byte> data)
    {
        int offset = (seq - 1) * 7;
        data.CopyTo(s.Buffer.AsSpan(offset));
        if (s.Mode == TpMode.RtsCts)
            s.GrantSinceCts++;
    }

    /// <summary>构造 CTS 续授权帧。必须在 _gate 内调用（读取会话状态）；发送由调用方在锁外
    /// FireAndForget（Task 4 review：原 SendCtsContinuation 在锁内发送，回调可能自死锁）。</summary>
    private static CanFrame BuildCtsContinuation(SessionKey key, TpSession s, byte grant)
    {
        return new CanFrame(
            new CanId(J1939Id.Compose(s.Priority, TpCmPgn, key.Da, key.Sa), FrameFormat.Extended),
            TpCmMessage.Cts(grant, (byte)(s.ReceivedPackets + 1), s.Pgn).Encode(),
            FrameFlags.None, ChannelId.None, default);
    }

    /// <summary>构造 EOM.ACK 帧。必须在 _gate 内调用（读取会话状态）；发送由调用方在锁外
    /// FireAndForget（Task 4 review：原 SendEomAck 在锁内发送，回调可能自死锁）。</summary>
    private static CanFrame BuildEomAck(SessionKey key, TpSession s)
    {
        return new CanFrame(
            new CanId(J1939Id.Compose(s.Priority, TpCmPgn, key.Da, key.Sa), FrameFormat.Extended),
            TpCmMessage.EomAck((ushort)s.TotalBytes, (byte)s.TotalPackets, s.Pgn).Encode(),
            FrameFlags.None, ChannelId.None, default);
    }

    /// <summary>容量防御：接收会话超上限时驱逐最近活动最旧者（近似 LRU）。
    /// 必须在 _gate 内调用：仅摘除受害者并返回其 (key, session)；3106 日志与 Evicted 事件
    /// 由调用方在锁外记日志/引发（Task 4 review：锁内引发会与需要 _gate 的同步订阅者死锁）。</summary>
    private (SessionKey Key, TpSession Victim)? EvictIfFull_Locked()
    {
        if (_rxSessions.Count < _options.MaxConcurrentSessions)
            return null;

        byte evictSa = 0, evictDa = 0;
        double oldest = double.MaxValue;
        TpSession? victim = null;
        foreach (var (key, s) in _rxSessions)
        {
            if (s.LastFrameTimestampSec < oldest)
            {
                oldest = s.LastFrameTimestampSec;
                victim = s;
                (evictSa, evictDa) = key;
            }
        }

        if (victim is null)
            return null;
        var victimKey = new SessionKey(evictSa, evictDa);
        _rxSessions.Remove(victimKey);
        return (victimKey, victim);
    }
}
