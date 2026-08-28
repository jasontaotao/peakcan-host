using System.Globalization;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes AssertStable steps (Task C, spec 2026-08-27 §3.3).
/// 窗口语义同 AssertSignalWithin：订阅 decoded frame 流，回调时刻取 GetSignalValue 缓存快照
/// （null 不计样本），Task.Delay(WindowMs) 后 detach 判定。
/// 判定：样本数 &lt; MinSamples → Failed（不无限等待，窗口耗尽即报，学 AssertCycleTime）；
/// 否则窗口内 max-min ≤ MaxDelta 判稳定。覆盖"持续保持"类需求（如模式切换后转速回落稳定）。
/// </summary>
internal sealed class AssertStableStepExecutor : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.AssertStable;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertStableStep)step.Parameters;
        var windowMs = int.Parse(p.WindowMs, CultureInfo.InvariantCulture);
        var maxDelta = double.Parse(p.MaxDelta, CultureInfo.InvariantCulture);
        var minSamples = int.Parse(p.MinSamples, CultureInfo.InvariantCulture);

        if (windowMs <= 0 || maxDelta < 0 || minSamples <= 0)   // 非法参数 fail fast（同 AssertCycleTime review L4）
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"Invalid params: WindowMs={windowMs}, MaxDelta={maxDelta}, MinSamples={minSamples}",
                null, null, 0, Channel: p.TargetChannel);

        var samples = new List<double>();
        var gate = new object();

        using var sub = ctx.SubscribeDecodedFrames(p.TargetChannel, _ =>
        {
            // G1: 采样按 TargetChannel 路由（订阅已路由，采样未路由 → 恒取默认通道，错总线）
            var val = ctx.GetSignalValue(p.TargetChannel, p.SignalName, maxAgeMs: 5000);
            if (val is { } v)
                lock (gate) samples.Add(v);
        });

        await Task.Delay(windowMs, ct);

        lock (gate)
        {
            if (samples.Count < minSamples)
                return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                    $"Only {samples.Count} samples for {p.SignalName}, need >= {minSamples} (window {windowMs}ms)",
                    null, null, 0, Channel: p.TargetChannel);

            var min = samples.Min();
            var max = samples.Max();
            var delta = max - min;
            var pass = delta <= maxDelta;
            return new StepResult(0, step.Kind, step.Label, pass ? StepStatus.Passed : StepStatus.Failed,
                pass
                    ? $"signal {p.SignalName} stable [{min:F1}, {max:F1}] delta={delta:F1} <= {maxDelta} (n={samples.Count})"
                    : $"signal {p.SignalName} unstable [{min:F1}, {max:F1}] delta={delta:F1} > {maxDelta} (n={samples.Count})",
                null, null, 0, Channel: p.TargetChannel);
        }
    }
}
