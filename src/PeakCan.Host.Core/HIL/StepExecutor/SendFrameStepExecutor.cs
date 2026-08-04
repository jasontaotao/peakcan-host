namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes SendFrame steps. Returns Passed when frame is sent successfully, Failed otherwise.
/// </summary>
internal sealed class SendFrameStepExecutor : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.SendFrame;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        var p = (SendFrameStep)step.Parameters;
        try
        {
            var flags = p.Fd ? FrameFlags.Fd : FrameFlags.None;
            // Phase B2: counter/checksum 预处理（单次发送：不递增 counter，仅重算 checksum）
            var payload = p.AutoCounter is null && p.AutoChecksum is null
                ? p.Data
                : FrameAutoConfigProcessor.ApplyOneShot(p.Data, p.AutoCounter, p.AutoChecksum);
            var result = await ctx.SendFrameAsync(new CanFrame(p.Id, payload, flags, default, default), ct);

            return new StepResult(0, step.Kind, step.Label,
                result.IsSuccess ? StepStatus.Passed : StepStatus.Failed,
                result.IsSuccess ? "Frame sent" : (result.Error?.Message ?? "SendFrame failed (no error detail)"),
                null, null, 0);
        }
        catch (Exception ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"Send failed: {ex.Message}", null, null, 0);
        }
    }
}
