namespace PeakCan.Host.Core.Uds;

public partial class UdsClient
{
    // Flow B: SessionControl (0x10 + 0x11) + S3 keepalive facades.
    // DiagnosticSessionControlAsync (0x10) + EcuResetAsync x 2 overloads (0x11)
    // + StartTesterPresent/StopTesterPresent (S3 keepalive scheduling, per
    // W12 D2 grouped with session-state-mutating ops not TesterPresent wire-emit).
    // Extracted from UdsClient.cs verbatim per W12 D5.

    /// <summary>
    /// DiagnosticSessionControl (0x10).
    /// </summary>
    /// <param name="sessionType">Session type (1=Default, 2=Extended, 3=Programming).</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task<DiagnosticSessionResponse> DiagnosticSessionControlAsync(byte sessionType, CancellationToken ct = default)
    {
        // 复位代际号：响应到达后、写状态之前若发生过断连复位，就放弃写入。
        // 这条续写在 SendRequestAsync 返回后才跑（此时 _requestLock 已释放），
        // 所以加锁拦不住它——只能让它自己比对代际号。
        var epochBeforeRequest = ResetEpoch;

        var response = await SendRequestAsync(0x10, new byte[] { sessionType }, ct).ConfigureAwait(false);

        // Parse response: [sessionType, P2high, P2low, P2*high, P2*low]
        if (response.Length < 5)
            throw new UdsException("Invalid DiagnosticSessionControl response");

        // ISO 14229-1 (DiagnosticSessionControl positive response): the ECU
        // reports P2 (P2Server_max) in milliseconds but P2* (P2*Server_max)
        // in 10 ms units. Reading P2* raw (without ×10) shortens every
        // post-0x78 wait to a tenth of what the ECU asked for — which is
        // exactly the window a slow Erase/TransferData uses to say
        // "still working".
        var result = new DiagnosticSessionResponse
        {
            SessionType = response[0],
            P2 = (response[1] << 8) | response[2],
            P2Star = ((response[3] << 8) | response[4]) * 10
        };

        // 断连复位竞态守卫：请求期间发生过复位，这条协商结果属于「上一台 ECU」，
        // 不能再写回。否则复位先清干净、这里又写回去，界面就会宣称一个已经不存在的
        // 协商值（正是本阶段要消灭的「标注是假的」）。
        // 仍返回 result：调用方拿到的响应本身是有效的，只是不采纳为当前状态。
        if (ResetEpoch != epochBeforeRequest)
            return result;

        Session.SetSession(result.SessionType, result.P2, result.P2Star);

        // Test seam: lets a test land a reset inside the non-atomic window between
        // the session write and the timer write (see the second epoch check below).
        BetweenSessionAndTimerWriteForTesting?.Invoke();

        // C-3 fix: propagate negotiated timings to UdsTimer so subsequent
        // requests honour the ECU's P2 / P2* (e.g. longer P2* in Programming
        // session). Without this, SendRequestInternalAsync would always use
        // the 50 ms default and time out on the first diagnostic request.
        _timer.P2Timeout = TimeSpan.FromMilliseconds(result.P2);
        _timer.P2StarTimeout = TimeSpan.FromMilliseconds(result.P2Star);

        // 二次比对：上面「检查 → 写 Session → 写 timer」不是原子的，复位可能恰好
        // 落在其中。发现代际已变就撤销这次写入，回到复位后的基线，而不是留下
        // 「Session 是新协商值、timer 是默认值」的半截状态。
        if (ResetEpoch != epochBeforeRequest)
        {
            ResetNegotiatedState();
        }

        return result;
    }

    /// <summary>
    /// ECUReset (0x11).
    /// </summary>
    /// <param name="resetType">Reset type (1=Hard, 2=KeyOff, 3=Soft).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// v1.3.0 MINOR Item 2: marked <c>virtual</c> for consistency with 7
    /// sibling UDS methods. Tests can override to intercept wire emit.
    /// Defensive length check on <c>response[0]</c> prevents
    /// <see cref="IndexOutOfRangeException"/> if <see cref="SendRequestAsync"/>
    /// returns an empty payload.
    /// </remarks>
    public virtual async Task<byte> EcuResetAsync(byte resetType, CancellationToken ct = default)
    {
        var response = await SendRequestAsync(0x11, new byte[] { resetType }, ct).ConfigureAwait(false);
        return response.Length > 0 ? response[0] : (byte)0;
    }

    /// <summary>
    /// v1.3.0 MINOR Item 2/4: type-safe enum overload.
    /// </summary>
    /// <param name="resetType">ISO 14229-1 §10.2 standard reset sub-function.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The echoed sub-function byte from the positive response.</returns>
    public Task<byte> EcuResetAsync(UdsResetType resetType, CancellationToken ct = default)
        => EcuResetAsync((byte)resetType, ct);

    /// <summary>Start automatic TesterPresent.</summary>
    public void StartTesterPresent(TimeSpan? interval = null)
    {
        Session.StartS3KeepAlive(this, interval);
    }

    /// <summary>Stop automatic TesterPresent.</summary>
    public void StopTesterPresent()
    {
        Session.StopS3KeepAlive();
    }
}
