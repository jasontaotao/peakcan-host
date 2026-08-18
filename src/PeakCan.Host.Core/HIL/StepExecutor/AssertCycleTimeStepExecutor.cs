using System.Globalization;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes AssertCycleTime steps. Measures inter-frame periods in the window.
/// 语义（plan 定稿）：窗口内帧数 &lt; MinSamples → Failed（不无限等待，窗口耗尽即报）；
/// 否则取 [since, since+window] 帧间隔，比对 [MinMs, MaxMs]。
/// B.5: WindowMs/MinMs/MaxMs/MinSamples are now string (supports ${name} interpolation).
/// </summary>
internal sealed class AssertCycleTimeStepExecutor : IStepExecutor
{
    private readonly IFrameStatistics _stats;

    public AssertCycleTimeStepExecutor(IFrameStatistics stats) => _stats = stats;
    public TestCaseStepKind Kind => TestCaseStepKind.AssertCycleTime;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertCycleTimeStep)step.Parameters;
        var windowMs = int.Parse(p.WindowMs, CultureInfo.InvariantCulture);
        var minMs = double.Parse(p.MinMs, CultureInfo.InvariantCulture);
        var maxMs = double.Parse(p.MaxMs, CultureInfo.InvariantCulture);
        var minSamples = int.Parse(p.MinSamples, CultureInfo.InvariantCulture);

        if (windowMs <= 0 || minMs > maxMs)   // review L4: 非法参数 fail fast
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"Invalid params: WindowMs={windowMs}, range=[{minMs},{maxMs}]", null, null, 0);

        var since = _stats.Now;
        var windowEnd = since + windowMs;   // 单调 ms 直接相加
        await Task.Delay(windowMs, ct);

        var s = _stats.GetIntervalStats(p.Id, since, windowEnd);
        // review L1: 至少 2 帧才有间隔；同时满足 MinSamples
        if (s.SampleCount < 2 || s.SampleCount < minSamples)
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"Only {s.SampleCount} frames for 0x{p.Id.Raw:X}, need >= {Math.Max(2, minSamples)} (window {windowMs}ms)", null, null, 0);

        // review M1: 逐区间判定（所有间隔落在 [MinMs, MaxMs]），而非均值掩盖个别越界周期
        bool pass = s.MinMs >= minMs && s.MaxMs <= maxMs;
        return new StepResult(0, step.Kind, step.Label, pass ? StepStatus.Passed : StepStatus.Failed,
            pass
                ? $"Cycle [{s.MinMs:F1},{s.MaxMs:F1}]ms in [{minMs},{maxMs}] (n={s.SampleCount})"
                : $"Cycle [{s.MinMs:F1},{s.MaxMs:F1}]ms outside [{minMs},{maxMs}] (n={s.SampleCount})",
            null, null, 0);
    }
}
