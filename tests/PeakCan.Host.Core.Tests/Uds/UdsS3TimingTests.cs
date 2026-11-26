using FluentAssertions;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds;

/// <summary>
/// S3 保活周期的**单一来源**。
/// <para>
/// 改前有三套互相矛盾的魔法数：UI 循环 2 秒、<c>StartS3KeepAlive</c> 默认 4 秒、
/// <c>ResetS3Timer</c> 5 秒。三者各自演化，没人知道哪个才是「对」的。现在 UI 侧
/// 统一由 <see cref="UdsSession.S3Timeout"/> 派生：保活间隔取 50%，S3 计时取 100%。
/// <para>
/// 刷写流水线（<c>PipelineExecutor</c>）刻意独立维护 1.5 秒节奏，不在本测试范围内。
/// </para>
/// </summary>
public sealed class UdsS3TimingTests
{
    [Fact]
    public void S3Timeout_Is_The_Single_Source_Of_Truth()
    {
        UdsSession.S3Timeout.Should().Be(TimeSpan.FromSeconds(5),
            "ISO 14229-1 S3server 默认 5 秒");
    }

    [Fact]
    public void KeepAliveInterval_Leaves_At_Least_Half_Of_S3_As_Margin()
    {
        // 真实保活间隔 = 请求耗时 + 间隔。若某次 3E 收到 NRC 0x78，客户端还要按
        // P2*（默认 5000 ms）继续等——间隔取 80% 会让这种情况下必然掉会话。
        // 取 S3/2 留出足够余量。
        UdsSession.KeepAliveInterval.Should().Be(TimeSpan.FromMilliseconds(2500));
    }

    [Fact]
    public void KeepAliveInterval_Stays_Strictly_Less_Than_S3()
    {
        UdsSession.KeepAliveInterval.Should().BeLessThan(UdsSession.S3Timeout,
            "保活间隔必须严格小于 S3 超时，否则保活本身就会触发会话超时");
    }

    [Fact]
    public void ResetS3Timer_Keeps_The_Configured_Period_Instead_Of_Stretching_To_S3()
    {
        // 回归：ResetS3Timer 曾被写成 Change(S3Timeout, Infinite)，把周期拉成 5 秒
        // 且不再重复——于是 StartS3KeepAlive 的 interval 参数只在第一个 tick 生效，
        // 稳态实际是 100% of S3，与「80%/50% 余量」的设计意图完全相反。
        var session = new UdsSession();
        try
        {
            session.StartS3KeepAlive(new NeverRespondingClient(), TimeSpan.FromMilliseconds(120));

            // 成功路径才会调 ResetS3Timer；这里用会抛的 client 走不到，所以直接
            // 验证 ResetS3Timer 自身不改变「已配置的周期」。
            session.ResetS3Timer();

            session.LastAppliedS3PeriodForTesting.Should().Be(TimeSpan.FromMilliseconds(120),
                "ResetS3Timer 是「重置计时」，不是「把周期改成 S3 超时时长」");
        }
        finally
        {
            session.StopS3KeepAlive();
        }
    }

    /// <summary>TesterPresent 立即失败，用于避免测试真的等一个周期。</summary>
    private sealed class NeverRespondingClient : UdsClient
    {
        public NeverRespondingClient() : base(
            new IsoTpLayer(new PeakCan.HIL.Core.Uds.IsoTp.CanIdConfig
            {
                RequestId = 0x7E0,
                ResponseId = 0x7E8,
            }, _ => { }),
            new UdsTimer())
        { }

        public override Task TesterPresentAsync(bool suppressPosResponse = false, CancellationToken ct = default)
            => throw new UdsException("test: no ECU");
    }
}
