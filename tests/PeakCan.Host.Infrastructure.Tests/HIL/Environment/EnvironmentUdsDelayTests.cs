using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

/// <summary>
/// UDS 响应 delayMs（2026-09-05 实现，此前 ProcessUdsRequests 丢弃 ProcessRequest
/// 返回的 DelayMs、立即发送）。delayMs>0 挂 pending 队列，由扫描 tick 到期发出，
/// 精度 = 扫描周期 10ms。
/// </summary>
public class EnvironmentUdsDelayTests
{
    private static RestbusNode CreateUdsNode(int delayMs) => new()
    {
        Name = "Ecu",
        Identity = new RawCanNodeIdentity(),
        UdsBehavior = new EcuScriptDefinition(
            new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 },
            [
                new EcuStateTransition
                {
                    ServiceId = 0x22,
                    Response = new StaticResponse([0x62, 0xF1, 0x90, 0x01, 0x02, 0x03]),
                    ResponseDelayMs = delayMs,
                },
            ]),
    };

    private static CanFrame MakeRequest() =>
        // ISO-TP 单帧：PCI 0x03 + RDNDBI 0x22 F1 90
        new(new CanId(0x7E0, FrameFormat.Standard),
            new byte[] { 0x03, 0x22, 0xF1, 0x90 },
            FrameFlags.None, default, default, FrameSource.Bus);

    [Fact]
    public void ZeroDelay_ResponseSentImmediately()
    {
        var sent = new SentList();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([CreateUdsNode(delayMs: 0)], null);

        runtime.InjectIncomingFrame(MakeRequest());
        runtime.ScanForTest();

        var resp = sent.FirstOrDefault(f => f.Id.Raw == 0x7E8);
        Assert.True(resp != default, "expected UDS response frame");
        Assert.Equal(0x62, resp.Data.Span[0]);
        runtime.Stop();
    }

    [Fact]
    public void PositiveDelay_ResponseDeferredUntilDue()
    {
        var sent = new SentList();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([CreateUdsNode(delayMs: 40)], null);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        runtime.InjectIncomingFrame(MakeRequest());
        runtime.ScanForTest();

        // 确定性守卫：延迟 40ms，ScanForTest 同步返回瞬间响应不可能已发出（非时间竞争）。
        Assert.DoesNotContain(sent, f => f.Id.Raw == 0x7E8);

        // 另测实际到达延迟 ≥30ms（不依赖 Thread.Sleep 的中点断言——Thread.Sleep 是最小睡眠，
        // 并行负载下会过冲使响应先发出而误判，spec Rev9 残余 B3 的 flake 根因）。
        var deadline = DateTime.UtcNow.AddMilliseconds(2000);
        while (DateTime.UtcNow < deadline && !sent.Any(f => f.Id.Raw == 0x7E8))
            Thread.Sleep(5);

        var resp = sent.FirstOrDefault(f => f.Id.Raw == 0x7E8);
        Assert.True(resp != default, "expected delayed UDS response frame");
        // 必须是延迟发出（≥30ms，容忍调度抖动），而非同步立即发出（delayMs=0 的对照见上一测试）。
        Assert.True(stopwatch.ElapsedMilliseconds >= 30,
            $"delayed UDS response arrived too early: {stopwatch.ElapsedMilliseconds}ms");
        Assert.Equal(new byte[] { 0x62, 0xF1, 0x90, 0x01, 0x02, 0x03 }, resp.Data.ToArray());
        runtime.Stop();
    }
}
