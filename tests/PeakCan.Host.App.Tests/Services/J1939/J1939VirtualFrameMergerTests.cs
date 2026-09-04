using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.J1939;
using PeakCan.HIL.Core.Replay;
using PeakCan.Host.App.Services.J1939;
using Xunit;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.Services.J1939;

/// <summary>
/// Task 13：brief Step 1 用例原样（Msg / Merge / 级别① / 级别③ / Pf 掩码 / 无匹配）。
/// 字面适配（有先例证据的三处）：
/// ① <c>ValueType</c> 与 <c>System.ValueType</c> 在 ImplicitUsings 下歧义 → 别名
///    （<c>SignalDecoderTests.cs</c> 同款先例）；
/// ② <see cref="DbcDocument"/> 实为 5 参 record（含 <c>MessagesById</c> 字典），brief
///    的 4 参写法不能编译（<c>DbcDecodeBackgroundServiceTests.cs</c> 顶部同款先例注记）；
/// ③ 无匹配用例的 PGN 0x00F999 为 PDU2（PF=0xF9 ≥ 0xF0），<see cref="J1939Id.Compose"/>
///    对 PDU2 + da 会抛 <see cref="ArgumentException"/>（J1939Id.cs PDU2 分支）→ 去掉
///    da 实参，断言语义（无匹配 → null）不变。
/// 另补两个钉住路由决策语义的用例：级别②（三级全覆盖）与文档级缓存（brief 注记
/// “级别缓存行为由测试钉住”）。
/// </summary>
public class J1939VirtualFrameMergerTests
{
    private static J1939Message Msg(uint pgn, byte sa, byte da, byte priority = 6) => new(
        pgn, sa, da, priority, TpMode.Bam, new byte[] { 0x40, 0x1F }, 1.0, 1.07);

    [Fact]
    public void Merge_Inserts_Virtual_Frame_At_Completion_Time()
    {
        var raw = new List<ReplayFrame>
        {
            new(1.0, J1939Id.Compose(6, 0x00EB00, 0xF4, 0xFF), 8, new byte[8], FrameFlags.None, true),
            new(2.0, 0x123, 2, new byte[] { 9 }, FrameFlags.None, false),
        };
        var messages = new List<ReassembledJ1939Message>
        {
            new(Msg(0x000200, 0xF4, 0xFF), ReassemblyStatus.Complete),
            new(Msg(0x00F001, 0x11, 0xFF), ReassemblyStatus.Truncated),   // 非完整 → 不产虚拟帧
        };

        var merged = J1939VirtualFrameMerger.Merge(raw, messages);

        merged.Should().HaveCount(3);
        merged[0].Timestamp.Should().Be(1.0);          // 同刻原始帧在前（稳定排序）
        merged[1].Timestamp.Should().Be(1.07);         // 虚拟帧（完成时刻）
        merged[1].Id.Should().Be(J1939Id.Compose(6, 0x000200, 0xF4, 0xFF));
        merged[1].IsExtended.Should().BeTrue();
        merged[1].Data.Should().Equal(0x40, 0x1F);
        merged[2].Timestamp.Should().Be(2.0);
    }

    [Fact]
    public void FindMessage_Level1_Exact_29Bit_Id()
    {
        var dbc = MakeDbc(0x980256F4u);   // DBC 扩展帧惯例：bit31 置位

        // 行为化钉住 bit31 IDE 掩码约定：DBC Id 先 & 0x1FFFFFFF 剥 IDE 位，再与
        // 29 位虚拟 ID 精确比较（Task 1 spike 契约的行为级验证）。
        J1939VirtualFrameMerger.FindMessage(dbc, J1939Id.Compose(6, 0x000200, 0xF4, 0x56))   // 0x180256F4（CMDT）
            .Should().NotBeNull();
    }

    [Fact]
    public void FindMessage_Level2_Priority_Mask_Pgn_Sa_Convention()
    {
        // 惯例 B（PDU2）：PGN<<8|SA 全 26 位编码（PS=组扩展），无优先级、无 bit31。
        var dbc = MakeDbc(0x00F001F4u);

        // 虚拟帧 0x18F001F4：级别①差优先级位；级别③被 IsPdu1 门禁挡住（PF=0xF0 →
        // PDU2）→ 唯一命中路径为级别②（& 0x03FFFFFF 掩优先级）。
        J1939VirtualFrameMerger.FindMessage(dbc, J1939Id.Compose(6, 0x00F001, 0xF4))
            .Should().NotBeNull();
    }

    [Fact]
    public void FindMessage_Level3_Pf_Match_For_Bam_Virtual_Id()
    {
        var dbc = MakeDbc(0x980256F4u);
        var bamId = J1939Id.Compose(6, 0x000200, 0xF4, 0xFF);   // BAM 广播：PS=0xFF → 0x1802FFF4

        J1939VirtualFrameMerger.FindMessage(dbc, bamId)
            .Should().NotBeNull();   // 级别③（PF=0x02 段匹配）命中（修订 9）
    }

    [Fact]
    public void FindMessage_Matches_Pgn_Sa_Dbc_Convention_Via_Pf_Mask()
    {
        var dbc = MakeDbc(0x000200F4u);   // 惯例 B：PGN<<8|SA（无 bit31）

        J1939VirtualFrameMerger.FindMessage(dbc, J1939Id.Compose(6, 0x000200, 0xF4, 0xFF))
            .Should().NotBeNull();
    }

    [Fact]
    public void FindMessage_Returns_Null_On_No_Match()
    {
        var dbc = MakeDbc(0x000200F4u);

        // PGN 0x00F999 为 PDU2（无 da 实参；见文件头适配注记③）→ 三级全不命中。
        J1939VirtualFrameMerger.FindMessage(dbc, J1939Id.Compose(6, 0x00F999, 0x11))
            .Should().BeNull();
    }

    [Fact]
    public void FindMessage_Caches_Match_Level_Per_Document()
    {
        var dbc = MakeDbc(0x980256F4u);   // 首查按级别①解析 → 文档级缓存定格为 1

        J1939VirtualFrameMerger.FindMessage(dbc, J1939Id.Compose(6, 0x000200, 0xF4, 0x56))
            .Should().NotBeNull();
        // 同一文档再查 BAM 虚拟 ID：级别缓存为 ①（spec §9.3 原设计假设“同一 DBC 内
        // 消息 ID 惯例一致”，brief Task 13 注记保持）→ 不重扫，级别①谓词下无精确匹配。
        // 若实现改为每次重解析（无缓存），本断言将因级别③命中而失败——缓存行为的钉子。
        J1939VirtualFrameMerger.FindMessage(dbc, J1939Id.Compose(6, 0x000200, 0xF4, 0xFF))
            .Should().BeNull();
    }

    // Task 13 review Finding 1：未命中不得缓存（ResolveLevel 的 -1 不入缓存）——否则
    // 首次 miss 会永久毒化该 DbcDocument 的后续匹配（§9.3 的“惯例一致”假设只为
    // 正向级别缓存背书，不为负向缓存背书）。
    [Fact]
    public void FindMessage_Miss_Does_Not_Poison_Subsequent_Match()
    {
        var dbc = MakeDbc(0x980256F4u);

        // 首查：文档不含 PGN 0x00F999 的任何惯例 ID → 三级全不命中 → null（且不得入缓存）。
        J1939VirtualFrameMerger.FindMessage(dbc, J1939Id.Compose(6, 0x00F999, 0x11))
            .Should().BeNull();

        // 后查：文档经级别③实际命中的 BAM 虚拟 ID 仍须解析成功
        // （负向缓存被毒化时此调用将错误返回 null）。
        J1939VirtualFrameMerger.FindMessage(dbc, J1939Id.Compose(6, 0x000200, 0xF4, 0xFF))
            .Should().NotBeNull();
    }

    /// <summary>
    /// 单消息 DBC（brief MakeDbc 的 5 参适配版：<see cref="DbcDocument"/> 实际签名含
    /// <c>MessagesById</c> 字典）。
    /// </summary>
    private static DbcDocument MakeDbc(uint messageId)
    {
        var msg = new Message(messageId, "BRM", 49, "EVCC",
            new[]
            {
                new Signal("SOC", 384, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned, 1.0, 0.0, 0, 100, "%", Array.Empty<string>()),
            },
            IsMultiplexed: false, MultiplexorSignalIndex: null);
        return new DbcDocument(
            "1.0",
            Array.Empty<Node>(),
            new[] { msg },
            new Dictionary<uint, Message> { [messageId] = msg },
            new Dictionary<string, ValueTable>());
    }
}
