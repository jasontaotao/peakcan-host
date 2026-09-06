using System.Globalization;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL;

namespace PeakCan.Host.Core.HIL.StepExecutor;

/// <summary>
/// Executes AssertSignalWithin steps (Task C, spec 2026-08-27 §3.3).
/// 窗口语义：订阅 decoded frame 流，回调时刻取 ctx.GetSignalValue 缓存快照为样本
/// （与 WaitForSignalAsync 同一取值口径，spec §3.3 消歧裁决）——无关帧重复快照无害，
/// 快照为 null（报文缺失）不计入样本。Task.Delay(WindowMs) 后 detach 判定：
/// Any = 窗口内 ≥1 个样本命中；All = 全部样本命中才通过。
/// 零样本：Any 零命中自然 Failed；All 防空窗口 vacuous pass（spec §3.3）→ 同样 Failed。
/// 修的是瞬时快照断言（AssertSignal）的 flaky 根因：总线调度抖动让恰好一拍的断言间歇失败。
/// </summary>
internal sealed class AssertSignalWithinStepExecutor : IStepExecutor
{
    private readonly TimeProvider _timeProvider;

    /// <summary>DI 用无参构造（TimeProvider 未注册时源生 DI 只能解析此构造）。</summary>
    public AssertSignalWithinStepExecutor() : this(TimeProvider.System) { }

    /// <summary>
    /// P2-3（2026-09-06）：时钟注入。测试注入 FakeTimeProvider 后，窗口
    /// `Task.Delay` 由虚拟时钟确定性闭合——不再真等 windowMs 墙钟。
    /// </summary>
    internal AssertSignalWithinStepExecutor(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public TestCaseStepKind Kind => TestCaseStepKind.AssertSignalWithin;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertSignalWithinStep)step.Parameters;
        var expected = double.Parse(p.Expected, CultureInfo.InvariantCulture);
        // G5（spec §6 裁决）: Tolerance 空/空白 = 精确匹配（窗口退化为 hit 需 v==expected），
        // ExpectedValue 只显示 Expected 不带 ±；否则 double.Parse("") 空串会抛 FormatException。
        var hasTolerance = !string.IsNullOrWhiteSpace(p.Tolerance);
        var tolerance = hasTolerance ? double.Parse(p.Tolerance, CultureInfo.InvariantCulture) : 0;
        var windowMs = int.Parse(p.WindowMs, CultureInfo.InvariantCulture);

        if (windowMs <= 0)   // 非法参数 fail fast（同 AssertCycleTime review L4）
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"Invalid params: WindowMs={windowMs}", null, null, 0, Channel: p.TargetChannel);

        // G5（spec §6）: 有 Tolerance 拼 `1500±10`；空 Tolerance 只显示 Expected（不带 ±）。
        var expectedValue = hasTolerance ? $"{expected}±{tolerance}" : $"{expected}";
        var samples = new List<double>();
        var gate = new object();

        using var sub = ctx.SubscribeDecodedFrames(p.TargetChannel, _ =>
        {
            // G1: 采样按 TargetChannel 路由（订阅已路由，采样未路由 → 恒取默认通道，错总线）
            var val = ctx.GetSignalValue(p.TargetChannel, p.SignalName, maxAgeMs: 5000);
            if (val is { } v)
                lock (gate) samples.Add(v);
        });

        await Task.Delay(TimeSpan.FromMilliseconds(windowMs), _timeProvider, ct);

        lock (gate)
        {
            if (samples.Count == 0)
                return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                    $"No samples for {p.SignalName} in {windowMs}ms window",
                    null, expectedValue, 0, Channel: p.TargetChannel);

            var hits = samples.Count(v => Math.Abs(v - expected) <= tolerance);
            var pass = p.Mode == MatchMode.Any ? hits >= 1 : hits == samples.Count;
            var range = $"[{expected - tolerance}, {expected + tolerance}]";
            return new StepResult(0, step.Kind, step.Label, pass ? StepStatus.Passed : StepStatus.Failed,
                pass
                    ? $"signal {p.SignalName} {hits}/{samples.Count} samples in {range} (mode {p.Mode})"
                    : $"signal {p.SignalName} {hits}/{samples.Count} samples in {range}, need {(p.Mode == MatchMode.Any ? ">= 1" : "all")} (mode {p.Mode})",
                $"{hits}/{samples.Count} samples", expectedValue, 0, Channel: p.TargetChannel);
        }
    }
}
