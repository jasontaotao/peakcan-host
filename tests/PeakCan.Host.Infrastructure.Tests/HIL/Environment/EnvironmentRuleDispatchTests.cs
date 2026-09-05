using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
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
        System.Threading.Thread.Sleep(15);
        runtime.ScanForTest(); // 到期后发出首个周期帧
        Assert.Contains(sent, f => f.Id.Raw == J1939Raw(6, 0x0100, 0x56, 0xF4));
        runtime.Stop();
    }
}
