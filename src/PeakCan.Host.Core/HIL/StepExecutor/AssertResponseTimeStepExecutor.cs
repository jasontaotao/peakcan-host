using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Core.HIL.StepExecutor;

/// <summary>
/// Executes AssertResponseTime steps. Sends request frame, measures wall-clock until response frame arrives.
/// </summary>
internal sealed class AssertResponseTimeStepExecutor : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.AssertResponseTime;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertResponseTimeStep)step.Parameters;

        // 关键：先订阅再发送，避免 ECU 快响应（<1ms）在订阅注册前到达导致丢帧
        var tcs = new TaskCompletionSource<CanFrame>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        using var sub = ctx.SubscribeDecodedFrames(frame =>
        {
            if (frame.Frame.Id.Raw == p.RespId.Raw)
                tcs.TrySetResult(frame.Frame);
        });

        // ⚠️ 计时器必须在 SendFrameAsync 之前启动，否则发送延迟不被计入
        // 同时 cts.CancelAfter 与 Stopwatch 同步启动，确保超时判断与测量一致
        var sw = System.Diagnostics.Stopwatch.StartNew();
        cts.CancelAfter(p.MaxMs);

        using var registration = cts.Token.Register(() => tcs.TrySetCanceled());

        // Send request frame（订阅已就绪 + 计时器已启动后才发送）
        var sendResult = await ctx.SendFrameAsync(
            new CanFrame(p.ReqId, ReadOnlyMemory<byte>.Empty, FrameFlags.None, default, default), ct);
        if (!sendResult.IsSuccess)
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"Failed to send request: {sendResult.Error?.Message}", null, null, 0);

        try
        {
            await tcs.Task.ConfigureAwait(false);
            sw.Stop();
            bool withinTime = sw.ElapsedMilliseconds <= p.MaxMs;
            return new StepResult(0, step.Kind, step.Label,
                withinTime ? StepStatus.Passed : StepStatus.Failed,
                withinTime ? $"Response in {sw.ElapsedMilliseconds}ms"
                           : $"Response too slow: {sw.ElapsedMilliseconds}ms > {p.MaxMs}ms",
                sw.ElapsedMilliseconds.ToString(), $"<= {p.MaxMs}ms", 0);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"No response from 0x{p.RespId.Raw:X} within {p.MaxMs}ms",
                null, $"<= {p.MaxMs}ms", 0);
        }
    }
}
