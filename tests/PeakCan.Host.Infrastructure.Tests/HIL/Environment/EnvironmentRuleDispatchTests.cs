using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class EnvironmentRuleDispatchTests
{
    private static CanFrame MakeFrame(uint id, byte[] data, FrameSource source = FrameSource.Bus) =>
        new(new CanId(id, FrameFormat.Standard), data, FrameFlags.None, default, default, source);

    [Fact]
    public void IncomingFrame_MatchesRule_SendsResponse()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x500, false), null,
                    new SendMessageAction(new CanMessageRef(0x600, false), new FixedHexSource("01")),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.InjectIncomingFrame(MakeFrame(0x500, [0x01]));
        runtime.ScanForTest();
        Assert.Contains(sent, f => f.Id.Raw == 0x600);
        runtime.Stop();
    }

    [Fact]
    public void IncomingFrame_NoMatch_NoResponse()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x500, false), null,
                    new SendMessageAction(new CanMessageRef(0x600, false), new FixedHexSource("01")),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.InjectIncomingFrame(MakeFrame(0x3FF, [0x01]));
        runtime.ScanForTest();
        Assert.DoesNotContain(sent, f => f.Id.Raw == 0x600);
        runtime.Stop();
    }

    [Fact]
    public void EnvironmentSourceFrame_Ignored()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x500, false), null,
                    new SendMessageAction(new CanMessageRef(0x600, false), new FixedHexSource("01")),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.InjectIncomingFrame(MakeFrame(0x500, [0x01], source: FrameSource.Environment));
        runtime.ScanForTest();
        Assert.DoesNotContain(sent, f => f.Id.Raw == 0x600);
        runtime.Stop();
    }

    [Fact]
    public void BytePatternCondition_MatchingPayload_Triggers()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x500, false),
                    new BytePattern(0, 0xFF, 0x42),
                    new SendMessageAction(new CanMessageRef(0x600, false), new FixedHexSource("01")),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.InjectIncomingFrame(MakeFrame(0x500, [0x42]));
        runtime.ScanForTest();
        Assert.Contains(sent, f => f.Id.Raw == 0x600);
        runtime.Stop();
    }

    [Fact]
    public void BytePatternCondition_NonMatchingPayload_DoesNotTrigger()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x500, false),
                    new BytePattern(0, 0xFF, 0x42),
                    new SendMessageAction(new CanMessageRef(0x600, false), new FixedHexSource("01")),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.InjectIncomingFrame(MakeFrame(0x500, [0x99]));
        runtime.ScanForTest();
        Assert.DoesNotContain(sent, f => f.Id.Raw == 0x600);
        runtime.Stop();
    }
}

public class EnvironmentJ1939RuleDispatchTests
{
    // J1939 29 位 ID 手工组合（与 J1939Id.Compose 位布局一致）：
    // priority<<26 | PF<<16 | PS<<8 | SA
    private static uint J1939Raw(byte priority, uint pgn, byte sa, byte? da)
    {
        uint pdu1 = ((pgn >> 8) & 0xFF) < 0xF0 ? 1u : 0u;
        uint ps = pdu1 == 1 ? da ?? 0xFF : pgn & 0xFF;
        return (uint)(priority << 26) | ((pgn >> 8) & 0xFF) << 16 | ps << 8 | sa;
    }

    private static CanFrame MakeExtFrame(uint id, byte[] data) =>
        new(new CanId(id, FrameFormat.Extended), data, FrameFlags.None, default, default, FrameSource.Bus);

    [Fact]
    public void J1939Trigger_MatchesIncoming_SendsComposedActionFrame()
        // 修复前 MatchesIncoming 对 J1939 触发恒 false——GB/T 27930 规则链整体失效
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "Charger",
            Identity = new J1939NodeIdentity(0x56),
            Rules =
            [
                // BMS 中止 (CST 0x1B00, SA 0xF4) → 回 BST (0x1A00, DA 0xF4)
                new ResponseRule(
                    new J1939MessageRef(0x1B00, 6, null, 0xF4, null), null,
                    new SendMessageAction(
                        new J1939MessageRef(0x1A00, 6, TpMode.Single, null, 0xF4),
                        new FixedHexSource("AA")),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        // CST 帧：P6, PF 0x1B, DA 0x56, SA 0xF4
        runtime.InjectIncomingFrame(MakeExtFrame(J1939Raw(6, 0x1B00, 0xF4, 0x56), [0x00]));
        runtime.ScanForTest();
        // 期望 BST 帧：P6, PF 0x1A, DA 0xF4, SA 取自节点身份 0x56（修复前恒 0x00）
        Assert.Contains(sent, f => f.Id.Raw == J1939Raw(6, 0x1A00, 0x56, 0xF4));
        runtime.Stop();
    }

    [Fact]
    public void J1939Trigger_SourceAddressMismatch_DoesNotTrigger()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "Charger",
            Identity = new J1939NodeIdentity(0x56),
            Rules =
            [
                new ResponseRule(
                    new J1939MessageRef(0x1B00, 6, null, 0xF4, null), null,
                    new SendMessageAction(
                        new J1939MessageRef(0x1A00, 6, TpMode.Single, null, 0xF4),
                        new FixedHexSource("AA")),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        // SA 0x56 ≠ 触发要求 0xF4
        runtime.InjectIncomingFrame(MakeExtFrame(J1939Raw(6, 0x1B00, 0x56, 0x56), [0x00]));
        runtime.ScanForTest();
        Assert.DoesNotContain(sent, f => f.Id.Raw == J1939Raw(6, 0x1A00, 0x56, 0xF4));
        runtime.Stop();
    }

    [Fact]
    public void J1939StartAction_EnablesPeriodicMessage_WithIdentitySa()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "Charger",
            Identity = new J1939NodeIdentity(0x56),
            Messages =
            [
                // CRM：默认禁用，收到 BRM 后启动
                new NodeMessage(
                    new J1939MessageRef(0x0100, 6, TpMode.Single, null, 0xF4),
                    250, new FixedHexSource("AA"), Enabled: false),
            ],
            Rules =
            [
                new ResponseRule(
                    new J1939MessageRef(0x0200, 6, null, 0xF4, null), null,
                    new StartMessageAction(new J1939MessageRef(0x0100, 6, TpMode.Single, null, 0xF4)),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.InjectIncomingFrame(MakeExtFrame(J1939Raw(6, 0x0200, 0xF4, 0x56), [0x01]));
        runtime.ScanForTest(); // 规则命中，消息启动（下次发送排在未来 10ms 量子）
        // 周期发送在真实 10ms 定时器线程上，高负载下可能晚于固定 sleep——轮询等待
        var deadline = System.Environment.TickCount64 + 500;
        while (System.Environment.TickCount64 < deadline && !sent.Any(f => f.Id.Raw == J1939Raw(6, 0x0100, 0x56, 0xF4)))
            System.Threading.Thread.Sleep(10);
        Assert.Contains(sent, f => f.Id.Raw == J1939Raw(6, 0x0100, 0x56, 0xF4));
        runtime.Stop();
    }
}

/// <summary>
/// setSignal 规则原语（2026-09-05 实现，此前 case 分支为 no-op 占位）：
/// 规则触发后信号值写入运行时信号表，目标 DbcSignalsSource 报文下次发送时按新值编码。
/// </summary>
public class EnvironmentSetSignalRuleDispatchTests
{
    private static CanFrame MakeFrame(uint id, byte[] data, FrameSource source = FrameSource.Bus) =>
        new(new CanId(id, FrameFormat.Standard), data, FrameFlags.None, default, default, source);

    private static DbcDocument CreateTestDbc()
    {
        var text = """
VERSION ""

NS_ :

BS_:

BU_: Charger BMS

BO_ 512 CRM: 8 Charger
 SG_ CRM_Signal : 0|16@1+ (1,0) [0|65535] "" BMS
""";
        var result = DbcParser.Parse(text);
        Assert.True(result.IsSuccess, result.Error?.Message ?? "parse failed");
        return result.Value!;
    }

    [Fact]
    public void SetSignalRule_WritesRuntimeSignal_NextSendEncodesNewValue()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "Charger",
            Identity = new RawCanNodeIdentity(),
            Messages =
            [
                // 周期发送的 DBC 信号报文：规则触发前按 offset 初值编码
                new NodeMessage(new CanMessageRef(512, false), 10, new DbcSignalsSource("CRM")),
            ],
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x500, false), null,
                    new SetSignalAction("CRM", "CRM_Signal", 100),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance, CreateTestDbc());
        runtime.Start([node], null);

        var first = runtime.GetEncodedPayload("Charger", "CRM");
        Assert.NotNull(first);
        Assert.Equal(0x00, first[0]);

        runtime.InjectIncomingFrame(MakeFrame(0x500, [0x01]));
        runtime.ScanForTest(); // ScanForTest = ProcessIncoming：触发规则写入信号表

        var after = runtime.GetEncodedPayload("Charger", "CRM");
        Assert.NotNull(after);
        Assert.Equal(0x64, after[0]); // 100 → 0x64 (little-endian)

        // 周期发送由真实 10ms 定时器驱动（ScanForTest 不发帧）：
        // 轮询等待下一个 tick，报文必须按新信号值编码发送
        var deadline = DateTime.UtcNow.AddMilliseconds(500);
        while (DateTime.UtcNow < deadline &&
               !sent.Any(f => f.Id.Raw == 512 && f.Data.Length > 0 && f.Data.Span[0] == 0x64))
            Thread.Sleep(5);
        Assert.Contains(sent, f => f.Id.Raw == 512 && f.Data.Span[0] == 0x64);
        runtime.Stop();
    }

    [Fact]
    public void SetSignalRule_NonDbcPayload_LogsWarningAndDoesNotThrow()
    {
        var channel = new FakeChannel();
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Messages = [new NodeMessage(new CanMessageRef(0x600, false), 10, new FixedHexSource("FF"))],
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x500, false), null,
                    new SetSignalAction("NotADbcMessage", "Sig", 1),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.InjectIncomingFrame(MakeFrame(0x500, [0x01]));
        runtime.ScanForTest();
        runtime.Stop();
    }
}
