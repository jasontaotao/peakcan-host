using Microsoft.Extensions.Logging;

namespace PeakCan.HIL.Core.J1939;

public sealed partial class J1939TpLayer
{
    /// <summary>
    /// BAM 广播发送（spec §6.1）。返回的 Task 在最后一帧 TP.DT 写出后完成；
    /// 取消/发送失败立即中止（不重试，重试策略归上层）；取消时返回
    /// <see cref="ErrorCode.Cancelled"/> 的 Result（不抛异常）。
    /// <para>长度契约：0B、1–8B（应走单帧直发）、&gt;<see cref="J1939TpOptions.MaxPayloadBytes"/> → Error。</para>
    /// <para>并发：多个 BAM 并发合法（帧交错）；同 (PGN, SA) 的并发发送由调用方避免。</para>
    /// </summary>
    public async Task<Result<Unit>> SendBamAsync(
        uint pgn, byte priority, byte sa, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        // Task 5 修订（有据，见 task-5-report）：brief 原稿无 try/catch——取消时 Task.Delay 抛出的
        // TaskCanceledException 会沿 async Task 直接逃逸，`await send` 得到异常而非 Result；
        // 而 brief 自带测试 Cancellation_Aborts_Mid_Stream 断言 `await send` 返回
        // Error.Code == ErrorCode.Cancelled 的 Result（xmldoc 亦言明"取消立即中止"）。
        // 最小修订：整包捕获 OperationCanceledException → Fail(ErrorCode.Cancelled)，其余逐字未动。
        try
        {
            if (!TryValidatePayload(payload.Length, out var validationError))
                return Result<Unit>.Fail(ErrorCode.InvalidArgument, validationError);

            var cmFrame = new CanFrame(
                new CanId(J1939Id.Compose(priority, TpCmPgn, sa, 0xFF), FrameFormat.Extended),
                TpCmMessage.Bam((ushort)payload.Length, (byte)((payload.Length + 6) / 7), pgn).Encode(),
                FrameFlags.None, ChannelId.None, default);
            var sent = await _sendAsync(cmFrame, ct).ConfigureAwait(false);
            if (!sent.IsSuccess)
                return FailFrom(sent);

            int packets = (payload.Length + 6) / 7;
            var dtId = new CanId(J1939Id.Compose(priority, TpDtPgn, sa, 0xFF), FrameFormat.Extended);
            for (int i = 0; i < packets; i++)
            {
                int take = Math.Min(7, payload.Length - i * 7);
                var chunk = new byte[take];
                payload.Span.Slice(i * 7, take).CopyTo(chunk);
                var dtFrame = new CanFrame(dtId, new TpDtMessage((byte)(i + 1), chunk).Encode(), FrameFlags.None, ChannelId.None, default);

                var dtResult = await _sendAsync(dtFrame, ct).ConfigureAwait(false);
                if (!dtResult.IsSuccess)
                    return FailFrom(dtResult);

                if (i < packets - 1 && _options.BamIntervalMs > 0)
                    // Task 5 修订（有据）：Task.Delay 无 (int, TimeProvider, ct) 重载（CS1503，实测），
                    // TimeProvider 重载仅收 TimeSpan → 最小修订加 TimeSpan.FromMilliseconds 包装，语义不变。
                    await Task.Delay(TimeSpan.FromMilliseconds(_options.BamIntervalMs), _timeProvider, ct).ConfigureAwait(false);
            }

            // Task 5 修订（有据）：brief 原稿写 Ok(Unit.Value)，包内 Unit 为空结构体、无 Value 成员
            // （CS0117，实测；全仓 20+ 处既有先例均为 Ok(default)）。语义等价（Unit 无状态）。
            return Result<Unit>.Ok(default);
        }
        catch (OperationCanceledException)
        {
            return Result<Unit>.Fail(ErrorCode.Cancelled, "BAM 发送已取消");
        }
    }

    private bool TryValidatePayload(int length, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        if (length is >= 1 and <= 8)
        {
            error = "≤8 字节应直接发单帧，不走 TP";
            return false;
        }

        // Task 5 修订（有据）：brief 原稿 0 → "payload 为空"，但 brief 自带测试
        // Rejects_Payload_Outside_9_To_1785 的 [InlineData(0)] 断言 Message.Contains("单帧")。
        // 最小修订：0 的消息补上单帧指引，其余逐字未动。
        (error, var ok) = length switch
        {
            0 => ("payload 为空；单帧需 1–8 字节", false),
            > 1785 => ("payload 超过 J1939-21 上限 1785 字节", false),
            _ => (null, true),
        };
        return ok;
    }

    private static Result<Unit> FailFrom(Result<Unit> failed) =>
        Result<Unit>.Fail(failed.Error?.Code ?? ErrorCode.InvalidState, failed.Error?.Message ?? "send failed");
}
