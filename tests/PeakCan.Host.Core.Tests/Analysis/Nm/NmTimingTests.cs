using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// NM M2 S2 前置：全局时序与报文级生效时序。spec §5.2 / §5.2.1。
/// 关键约束：报文级覆盖必须优先于全局（现有 NmAnalyzer.cs:255 的语义）。
/// </summary>
public class NmTimingTests
{
    // CA1861: 常量数组不内联，避免每次构造都分配。
    private static readonly string[] TestReasons = ["test"];

    [Fact]
    public void FromProfile_NoProfile_UsesM1Defaults()
    {
        var timing = NmTimingDefaults.FromProfile(null, null);

        Assert.Equal(500, timing.CycleMs);
        Assert.Equal(2000, timing.TimeoutMs);
        Assert.Equal(2000, timing.WaitBusSleepMs);
        Assert.Equal(1700, timing.RepeatMessageWithBurstMs);
        Assert.Equal(1500, timing.RepeatMessageWithoutBurstMs);
        Assert.Equal(20, timing.ImmediateCycleMs);
        Assert.Equal(10, timing.ImmediateCount);
        Assert.Equal(10, timing.StartNmTxMs);
        Assert.Equal(120, timing.WakeUpMs);
        // M1 的容差是 max(0.2×cycle, 30ms)，不是吉利的 0.1。
        Assert.Equal(0.2, timing.ToleranceRatio);
    }

    [Fact]
    public void FromProfile_GeelyProfile_OverridesDefaults()
    {
        var timing = NmTimingDefaults.FromProfile(null, new NmStandardTimings
        {
            CycleMs = 500,
            TimeoutMs = 2200,
            ToleranceRatio = 0.1,
            ImmediateCount = 12,
        });

        Assert.Equal(2200, timing.TimeoutMs);
        Assert.Equal(0.1, timing.ToleranceRatio);
        Assert.Equal(12, timing.ImmediateCount);
        // 未覆盖的字段仍取默认。
        Assert.Equal(1700, timing.RepeatMessageWithBurstMs);
        Assert.Equal(120, timing.WakeUpMs);
    }

    [Fact]
    public void FromProfile_M1RepeatMessageMs_TakesPrecedence()
    {
        // M1 单一 RepeatMessageMs 有值时优先：否则 M1 配置（如 1000ms）
        // 在迁移后会被静默改成 1700/1500，违反「无 profile 时行为不变」。
        var timing = NmTimingDefaults.FromProfile(
            new NmTimings(RepeatMessageMs: 1000),
            new NmStandardTimings { RepeatMessageWithBurstMs = 1700 });

        Assert.Equal(1000, timing.RepeatMessageWithBurstMs);
        Assert.Equal(1000, timing.RepeatMessageWithoutBurstMs);
    }

    [Fact]
    public void FromProfile_NoM1Value_UsesProfileBurstValues()
    {
        var timing = NmTimingDefaults.FromProfile(null, new NmStandardTimings
        {
            RepeatMessageWithBurstMs = 1700,
            RepeatMessageWithoutBurstMs = 1500,
        });

        Assert.Equal(1700, timing.RepeatMessageWithBurstMs);
        Assert.Equal(1500, timing.RepeatMessageWithoutBurstMs);
    }

    [Fact]
    public void Resolve_MessageLevelOverridesGlobal()
    {
        var global = NmTimingDefaults.FromProfile(null, new NmStandardTimings { CycleMs = 500, TimeoutMs = 2000 });
        var def = new NmMessageDef("0x500", false, "NM", "A", 8, true,
            TestReasons, CycleMs: 640, TimeoutMs: 3000);

        var effective = NmTiming.Resolve(def, global);

        // 报文级优先：这是 NmAnalyzer.cs:255 `def.CycleMs ?? cycleMs` 的语义。
        Assert.Equal(640, effective.CycleMs);
        Assert.Equal(3000, effective.TimeoutMs);
        // 其余沿用全局。
        Assert.Equal(global.WaitBusSleepMs, effective.WaitBusSleepMs);
        Assert.Equal(global.ToleranceRatio, effective.ToleranceRatio);
    }

    [Fact]
    public void Resolve_NullMessageFields_FallBackToGlobal()
    {
        var global = NmTimingDefaults.FromProfile(null, new NmStandardTimings { CycleMs = 500, TimeoutMs = 2000 });
        var def = new NmMessageDef("0x500", false, "NM", "A", 8, true, TestReasons);

        var effective = NmTiming.Resolve(def, global);

        Assert.Equal(500, effective.CycleMs);
        Assert.Equal(2000, effective.TimeoutMs);
    }
}
