using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.HIL.Core.J1939;

public sealed partial class J1939TpLayer
{
    /// <summary>100ms 周期扫描定时器（离线模式恒 null）。</summary>
    private ITimer? _watchdog;

    /// <summary>单一定时器扫描会话表（100ms 周期，不为每会话建 timer）；离线模式不启动。</summary>
    private partial void StartWatchdog()
    {
        _watchdog = _timeProvider.CreateTimer(ScanSessions, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// T1 扫描：距最后一帧活动超过 <see cref="J1939TpOptions.T1Ms"/> 的接收会话作废并引发 3105 +
    /// <see cref="SessionEventKind.Timeout"/> 事件（spec §12）。只扫 <see cref="_rxSessions"/>——
    /// 发送会话（<see cref="_txSessions"/>）的 T3/T4 由各自 Task.Delay 竞速，见修订 14。
    /// 锁纪律（Task 4 review 定）：锁内仅判过期/摘除/记日志，事件在锁外引发（订阅者可能重入取 _gate）。
    /// <para>实现说明（brief Step 3 注）：扫描基准用 <see cref="TpSession.LastFrameTimestampTicks"/>
    /// （<see cref="TimeProvider.GetTimestamp"/> 计时值）配 <see cref="TimeProvider.GetElapsedTime(long)"/>；
    /// brief 原稿由 LastFrameTimestampSec（帧时间戳秒）换算——单位/基准均不同，不能换算，故按其
    /// 实现说明改存 ticks 字段、随 <see cref="TpSession.LastFrameTimestampSec"/> 一起更新。</para>
    /// </summary>
    private void ScanSessions(object? state)
    {
        List<J1939SessionEvent>? timeouts = null;
        lock (_gate)
        {
            List<SessionKey>? expired = null;
            foreach (var (key, s) in _rxSessions)
            {
                var elapsed = _timeProvider.GetElapsedTime(s.LastFrameTimestampTicks);
                if (elapsed.TotalMilliseconds < _options.T1Ms)
                    continue;
                (expired ??= new List<SessionKey>()).Add(key);
            }

            if (expired is not null)
            {
                timeouts = new List<J1939SessionEvent>();
                foreach (var key in expired)
                {
                    if (!_rxSessions.Remove(key, out var s))
                        continue;
                    LogSessionTimeout(_logger ?? NullLogger<J1939TpLayer>.Instance, key.Sa, key.Da, s.Pgn);
                    timeouts.Add(new J1939SessionEvent(SessionEventKind.Timeout, key.Sa, key.Da, s.Pgn, s.Mode, "T1"));
                }
            }
        }

        if (timeouts is not null)
            foreach (var evt in timeouts)
                RaiseSessionEvent(evt);
    }

    /// <summary>
    /// 离线结算（spec 修订 6）：对所有未闭合接收会话产出结果并清空会话表。
    /// 缺失包的字节位置以 0xFF 填充（J1939-21 §8.7）。在线模式由 watchdog/丢包事件负责，不走此路径。
    /// </summary>
    public IReadOnlyList<J1939SessionResult> FlushPendingSessions()
    {
        lock (_gate)
        {
            var results = new List<J1939SessionResult>();
            foreach (var (key, s) in _rxSessions)
            {
                var payload = new byte[s.TotalBytes];
                Array.Fill(payload, (byte)0xFF);
                Array.Copy(s.Buffer, payload, Math.Min(s.Buffer.Length, s.TotalBytes));
                results.Add(new J1939SessionResult(
                    key.Sa, key.Da, s.Priority, s.Pgn, s.Mode,
                    s.GapDetected ? J1939SessionOutcome.PacketLoss : J1939SessionOutcome.Truncated,
                    payload, s.FirstFrameTimestampSec, s.LastFrameTimestampSec));
            }

            _rxSessions.Clear();
            return results;
        }
    }

    /// <summary>停掉看门狗扫描定时器（节点 Stop/层废弃时调用）。</summary>
    public void Dispose()
    {
        _watchdog?.Dispose();
        _watchdog = null;
    }
}
