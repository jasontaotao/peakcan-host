using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Nodes;
using PeakCan.Host.App.Services.Nodes.J1939;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Nodes;

public class J1939NodeContextTests
{
    // 修订（有据）：brief 原稿帮助方法只返回 (ctx, sent, tpMessages)，但其自带测试
    // Start_Registers_Local_Address_And_Stop_Unregisters / Tp_Message_Received_Raises_Arrival
    // 需要直接驱动 ctx 内部的 J1939TpLayer——ctx.OnFrame 按计划修订 2（P0-1）跳过 TP 帧
    // （生产中 TP 帧由 J1939TpSinkAdapter 喂层，SinkWiringService.StartAsync 挂接；若
    // OnFrame 再转发，adapter+sink 双喂会使层双份收到 TP 帧）。帮助方法追加返回 layer，
    // 断言语义不变。
    private static (J1939NodeContext ctx, J1939TpLayer layer, List<CanFrame> sent, List<J1939Message> tpMessages) CreateContext(byte sa = 0x56)
    {
        var sent = new List<CanFrame>();
        var tpMessages = new List<J1939Message>();
        // 修订（有据）：brief 原稿两处 Ok(Unit.Value)——包内 Unit 为空结构体、无 Value 成员
        // （CS0117，实测；SendFlow.cs Task 5 同款裁定，全仓先例均为 Ok(default)）。
        var layer = new J1939TpLayer(
            (frame, _) => { sent.Add(frame); return ValueTask.FromResult(Result<Unit>.Ok(default)); },
            new J1939TpOptions { BamIntervalMs = 0 });
        layer.MessageReceived += tpMessages.Add;
        var ctx = new J1939NodeContext(
            new NodeConfig { Name = "t", Identity = new J1939NodeIdentity(sa) },
            new NodeRuntimeState(),
            layer,
            (frame, _) => { sent.Add(frame); return ValueTask.FromResult(Result<Unit>.Ok(default)); },
            new DbcService(NullLogger<DbcService>.Instance),
            new DbcEncodeService(),
            router: null,
            logger: null);
        return (ctx, layer, sent, tpMessages);
    }

    [Fact]
    public void Start_Registers_Local_Address_And_Stop_Unregisters()
    {
        // 修订（有据）：brief 原稿"用第二层对打"——RTS 喂 peer.ProcessFrame，peer 发送回调转
        // ctx.OnFrame。peer 层从未注册 0x56，而 J1939TpLayer.HandleRts 仅在
        // _localAddresses.Contains(PS=目标地址) 时回 CTS（ReceiveFlow.cs），peerSent 恒空，
        // 测试不可能通过；且 ctx.OnFrame 按设计跳过 TP 帧（见 CreateContext 注）。
        // 语义不变的最小修订：RTS 直接喂 ctx 的层，断言层经其发送回调回出 CTS；Stop 后静默。
        var (ctx, layer, sent, _) = CreateContext(0x56);
        var rts = new CanFrame(
            new CanId(J1939Id.Compose(6, 0x00EC00, 0xF4, 0x56), FrameFormat.Extended),
            TpCmMessage.Rts(9, 2, 0xFF, 0x00F001).Encode(), FrameFlags.None, ChannelId.None, default);

        ctx.Start();
        layer.ProcessFrame(rts);
        sent.Should().Contain(f => new J1939Id(f.Id.Raw).Pgn == 0x00EC00);   // 收到 CTS（说明 0x56 已注册）

        ctx.Stop();
        sent.Clear();
        layer.ProcessFrame(rts);
        sent.Should().BeEmpty();        // 注销后不再响应
    }

    [Fact]
    public void Single_Frame_App_Message_Raises_Arrival_With_Single_Mode()   // 修订 2（P0-1）
    {
        var (ctx, _, _, _) = CreateContext(0x56);
        NodeMessageArrived? arrived = null;
        ctx.MessageArrived += m => arrived = m;

        ctx.OnFrame(new CanFrame(
            new CanId(0x180900F4, FrameFormat.Extended),      // BRO：BMS→充电机 单帧
            new byte[] { 0xAA }, FrameFlags.None, ChannelId.None, default));

        arrived.Should().NotBeNull();
        var jref = arrived!.Ref.Should().BeOfType<J1939MessageRef>().Subject;
        jref.Pgn.Should().Be(0x000900);
        jref.Mode.Should().Be(TpMode.Single);
        jref.Sa.Should().Be(0xF4);
    }

    [Fact]
    public void Tp_Control_Frames_Are_Not_Forwarded_As_Arrivals()
    {
        var (ctx, _, _, _) = CreateContext(0x56);
        NodeMessageArrived? arrived = null;
        ctx.MessageArrived += m => arrived = m;

        ctx.OnFrame(new CanFrame(
            new CanId(J1939Id.Compose(6, 0x00EC00, 0xF4, 0xFF), FrameFormat.Extended),
            TpCmMessage.Bam(49, 7, 0x000200).Encode(), FrameFlags.None, ChannelId.None, default));

        arrived.Should().BeNull();       // TP 帧走 J1939TpLayer，不重复上报
    }

    [Fact]
    public void Tp_Message_Received_Raises_Arrival()
    {
        // 修订（有据）：brief 原稿 (a) async Task 无 await——TreatWarningsAsErrors 下 CS1998
        // 编译失败；(b) 经 ctx.OnFrame 喂 TP 帧——OnFrame 按设计跳过 TP 帧，注释原文
        // "直接向层喂一段 BAM" 即此意；(c) Bam(9,2) 两包各只带 2 字节 DT，层按 CM 声明的
        // TotalBytes=9 交付 [1,2,FF…FF,3,4]，与断言 Equal(1,2,3,4) 矛盾。最小修订：同步
        // Fact，帧直接喂层，BAM 改 1 包 4 字节（断言语义 Bam 模式 + payload [1,2,3,4] 不变）。
        var (ctx, layer, _, _) = CreateContext(0x56);
        ctx.Start();
        NodeMessageArrived? arrived = null;
        ctx.MessageArrived += m => arrived = m;

        // 直接向层喂一段 BAM（PGN 0x000200, SA=0xF4）
        var cmId = J1939Id.Compose(6, 0x00EC00, 0xF4, 0xFF);
        var dtId = J1939Id.Compose(6, 0x00EB00, 0xF4, 0xFF);
        layer.ProcessFrame(new CanFrame(new CanId(cmId, FrameFormat.Extended), TpCmMessage.Bam(4, 1, 0x000200).Encode(), FrameFlags.None, ChannelId.None, default));
        layer.ProcessFrame(new CanFrame(new CanId(dtId, FrameFormat.Extended), new TpDtMessage(1, new byte[] { 1, 2, 3, 4 }).Encode(), FrameFlags.None, ChannelId.None, default));

        arrived.Should().NotBeNull();
        arrived!.Ref.Should().BeOfType<J1939MessageRef>().Which.Mode.Should().Be(TpMode.Bam);
        arrived.Payload.Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task Send_FixedHex_Over_8B_Routes_To_Bam()
    {
        var (ctx, _, sent, _) = CreateContext(0x56);
        ctx.Start();

        ctx.Send(new J1939MessageRef(0x000200, 6, TpMode.Bam, null, 0xFF), new FixedHexSource("01 02 03 04 05 06 07 08 09"));
        await Task.Delay(20);   // fire-and-forget

        sent.Should().Contain(f => new J1939Id(f.Id.Raw).Pgn == 0x00EC00);   // BAM CM 已发出
    }

    [Fact]
    public async Task Send_FixedHex_Small_Routes_To_Single_Frame()
    {
        var (ctx, _, sent, _) = CreateContext(0x56);
        ctx.Start();

        ctx.Send(new J1939MessageRef(0x002600, 6, TpMode.Single, null, 0xF4), new FixedHexSource("01 01 00"));
        await Task.Delay(20);

        sent.Should().ContainSingle();
        sent[0].Id.Raw.Should().Be(J1939Id.Compose(6, 0x002600, 0x56, 0xF4));   // Compose 含 DA（修订 1）
    }

    [Fact]
    public async Task Send_Invalid_Hex_Reports_Error_Not_Throw()
    {
        var (ctx, _, sent, _) = CreateContext(0x56);
        ctx.Start();
        var reported = new List<(NodeActivityKind, string)>();
        // 修订（有据）：brief 原稿 ctx.Reported += reported.Add——List<(NodeActivityKind, string)>
        // 的 Add 收单个元组参数，与 Action<NodeActivityKind,string> 不匹配（CS0123，实测）；
        // 换 lambda 订阅，语义不变。
        ctx.Reported += (kind, detail) => reported.Add((kind, detail));
        var failed = new List<Exception>();
        ctx.SendFailed += failed.Add;

        ctx.Send(new J1939MessageRef(0x002600, 6, TpMode.Single, null, 0xF4), new FixedHexSource("XYZ"));
        await Task.Delay(20);

        failed.Should().ContainSingle();    // SendFailed 而非异常逃逸
        sent.Should().BeEmpty();
    }

    [Fact]
    public void Send_CanMessageRef_Not_Supported()
    {
        var (ctx, _, _, _) = CreateContext(0x56);
        ctx.Start();
        var failed = new List<Exception>();
        ctx.SendFailed += failed.Add;

        ctx.Send(new CanMessageRef(0x123, false), new FixedHexSource("01"));

        failed.Should().ContainSingle().Which.Should().BeOfType<NotSupportedException>();
    }
}

/// <summary>
/// Task 17 模板回归：随应用分发的 GB/T 27930 模板（Templates/Nodes/*.node.json，
/// App csproj 以 Content PreserveNewest 拷进输出目录）必须经 NodeConfigLibrary 的生产
/// 加载路径（camelCase JsonOpts + kind 判别联合）反序列化为合法 NodeConfig，且报文表 /
/// 规则链符合 GB/T 27930 握手序（硬件验证参考：PGN/SA/字节长）。
/// <para>修订（有据）：brief 原稿规则触发器写 "priority": null——J1939MessageRef.Priority
/// 为非可空 byte，null 反序列化抛 JsonException，Load() 容错跳过 → 模板加载恒为空
/// （RED 实测）。MessageRefMatcher 不比较优先级，触发器按 GB/T 27930 规范优先级 6 填写，
/// 匹配语义（PGN+SA 宽容匹配）不变。</para>
/// </summary>
public class Gbt27930NodeTemplateTests
{
    private static string TemplateDir => Path.Combine(AppContext.BaseDirectory, "Templates", "Nodes");

    private static NodeConfig Load(string name)
    {
        var lib = new NodeConfigLibrary(TemplateDir, NullLogger<NodeConfigLibrary>.Instance);
        return lib.Load().Should().Contain(c => c.Name == name).Subject;
    }

    [Theory]
    [InlineData("gbt27930-charger", 0x56)]
    [InlineData("gbt27930-bms", 0xF4)]
    public void Template_Loads_Through_NodeConfigLibrary(string name, byte sa)
    {
        var config = Load(name);

        config.Tag.Should().Be("gbt27930");
        config.Identity.Should().BeOfType<J1939NodeIdentity>().Which.Sa.Should().Be(sa);
        config.AddressClaimEnabled.Should().BeFalse();
        config.Messages.Should().NotBeEmpty();
        config.Rules.Should().NotBeEmpty();
    }

    [Fact]
    public void Charger_Message_Table_Follows_Gbt27930_FrameSpecs()
    {
        var charger = Load("gbt27930-charger");

        var byPgn = charger.Messages.ToDictionary(
            m => ((J1939MessageRef)m.Ref).Pgn,
            m => (Ref: (J1939MessageRef)m.Ref, m.Payload, m.Enabled));
        // CHM(0x2600)/CRM(0x0100)/CML(0x0800)/CRO(0x0A00)/CCS(0x1200)/CST(0x1A00)/CSD(0x1D00)
        byPgn.Keys.Should().BeEquivalentTo([
            0x002600u, 0x000100u, 0x000800u, 0x000A00u, 0x001200u, 0x001A00u, 0x001D00u]);
        foreach (var (_, (r, _, _)) in byPgn)
        {
            r.Mode.Should().Be(TpMode.Single);      // 充电机侧报文全部 ≤8B 单帧
            r.Sa.Should().BeNull();                 // 发送时用节点自身 SA（0x56）
            r.Da.Should().Be(0xF4);                 // 指向 BMS
        }

        byPgn[0x002600].Enabled.Should().BeTrue();  // CHM 待机常发
        byPgn[0x000800].Enabled.Should().BeTrue();  // CML 待机常发
        ByteCount(byPgn[0x002600].Payload).Should().Be(3);    // CHM 3B
        ByteCount(byPgn[0x000100].Payload).Should().Be(8);    // CRM 8B
        ByteCount(byPgn[0x000800].Payload).Should().Be(8);    // CML 8B
        ByteCount(byPgn[0x000A00].Payload).Should().Be(1);    // CRO 1B
        ByteCount(byPgn[0x001200].Payload).Should().Be(7);    // CCS 7B
        ByteCount(byPgn[0x001A00].Payload).Should().Be(4);    // CST 4B
        ByteCount(byPgn[0x001D00].Payload).Should().Be(8);    // CSD 8B
    }

    [Fact]
    public void Bms_Message_Table_Follows_Gbt27930_FrameSpecs()
    {
        var bms = Load("gbt27930-bms");

        var byPgn = bms.Messages.ToDictionary(
            m => ((J1939MessageRef)m.Ref).Pgn,
            m => (Ref: (J1939MessageRef)m.Ref, m.Payload, m.Enabled));
        // BHM(0x2700)/BRM(0x0200)/BCP(0x0600)/BRO(0x0900)/BCL(0x1000)/BCS(0x1100)/BSM(0x1300)/BST(0x1900)/BSD(0x1C00)
        byPgn.Keys.Should().BeEquivalentTo([
            0x002700u, 0x000200u, 0x000600u, 0x000900u, 0x001000u, 0x001100u, 0x001300u, 0x001900u, 0x001C00u]);

        byPgn[0x002700].Enabled.Should().BeTrue();  // BHM 待机常发
        ByteCount(byPgn[0x002700].Payload).Should().Be(2);    // BHM 2B
        ByteCount(byPgn[0x000200].Payload).Should().Be(49);   // BRM 49B（7×TP.DT）
        ByteCount(byPgn[0x000600].Payload).Should().Be(13);   // BCP 13B
        ByteCount(byPgn[0x001100].Payload).Should().Be(9);    // BCS 9B
        ByteCount(byPgn[0x001000].Payload).Should().Be(5);    // BCL 5B
        ByteCount(byPgn[0x001300].Payload).Should().Be(7);    // BSM 7B
        ByteCount(byPgn[0x001900].Payload).Should().Be(4);    // BST 4B
        ByteCount(byPgn[0x001C00].Payload).Should().Be(7);    // BSD 7B
        foreach (var (_, (r, _, _)) in byPgn)
        {
            r.Sa.Should().BeNull();                 // 发送时用节点自身 SA（0xF4）
            if (r.Mode == TpMode.Bam)
                r.Da.Should().Be(0xFF);             // BAM 广播（BRM/BCP/BCS）
            else
            {
                r.Mode.Should().Be(TpMode.Single);
                r.Da.Should().Be(0x56);             // 单帧指向充电机
            }
        }

        // 评审修订（Important）：BSM 为 7B 单帧（GB/T 27930：BSM 7B）——≤8B 载荷走 Bam 会被
        // SendBamAsync.TryValidatePayload 以 InvalidArgument 拒绝（SendFlow.cs"≤8 字节应直接
        // 发单帧"），启用即每周期报错。模板已改 Single/da=0x56，此处显式固化防回归。
        byPgn[0x001300].Ref.Mode.Should().Be(TpMode.Single);
        byPgn[0x001300].Ref.Da.Should().Be(0x56);

        // 终审修复（Important）：BCP 13B Bam 广播补齐（GB/T 27930 §11.3）——充电机模板的
        // BCP→CRO 规则此前对模拟 BMS 永不触发（配置阶段停摆）。BCP 为休眠项（enabled=false，
        // 与 BRM/BCS 同款，由编辑器/规则按需启用），此处显式固化防回归。
        byPgn[0x000600].Ref.Mode.Should().Be(TpMode.Bam);
        byPgn[0x000600].Ref.Da.Should().Be(0xFF);
        byPgn[0x000600].Enabled.Should().BeFalse();
    }

    [Fact]
    public void Charger_Rule_Chain_Matches_Gbt27930_Handshake()
    {
        var charger = Load("gbt27930-charger");

        charger.Rules.Should().HaveCount(6);
        // BRM→CRM、BCP→CRO、BRO→CCS、BST→停 CCS+发 CST、BSD→发 CSD；触发方均为 BMS（SA=0xF4）
        TriggerPgn(charger.Rules[0]).Should().Be(0x000200);
        charger.Rules[0].Action.Should().BeOfType<StartMessageAction>()
            .Which.Ref.Should().BeOfType<J1939MessageRef>().Which.Pgn.Should().Be(0x000100);
        TriggerPgn(charger.Rules[1]).Should().Be(0x000600);
        charger.Rules[1].Action.Should().BeOfType<StartMessageAction>()
            .Which.Ref.Should().BeOfType<J1939MessageRef>().Which.Pgn.Should().Be(0x000A00);
        TriggerPgn(charger.Rules[2]).Should().Be(0x000900);
        charger.Rules[2].Action.Should().BeOfType<StartMessageAction>()
            .Which.Ref.Should().BeOfType<J1939MessageRef>().Which.Pgn.Should().Be(0x001200);
        TriggerPgn(charger.Rules[3]).Should().Be(0x001900);
        charger.Rules[3].Action.Should().BeOfType<StopMessageAction>()
            .Which.Ref.Should().BeOfType<J1939MessageRef>().Which.Pgn.Should().Be(0x001200);
        TriggerPgn(charger.Rules[4]).Should().Be(0x001900);
        var cst = charger.Rules[4].Action.Should().BeOfType<SendMessageAction>().Which;
        cst.Ref.Should().BeOfType<J1939MessageRef>().Which.Pgn.Should().Be(0x001A00);
        ByteCount(cst.Payload).Should().Be(4);
        TriggerPgn(charger.Rules[5]).Should().Be(0x001C00);
        var csd = charger.Rules[5].Action.Should().BeOfType<SendMessageAction>().Which;
        csd.Ref.Should().BeOfType<J1939MessageRef>().Which.Pgn.Should().Be(0x001D00);
        ByteCount(csd.Payload).Should().Be(8);
        charger.Rules.Select(TriggerSa).Should().OnlyContain(sa => sa == 0xF4);
    }

    [Fact]
    public void Bms_Rule_Chain_Matches_Gbt27930_Handshake()
    {
        var bms = Load("gbt27930-bms");

        bms.Rules.Should().HaveCount(8);
        // CHM→BRM、CRM→BRO、CRO→BCL+BCS、CST→停 BCL+BCS+发 BST+发 BSD；触发方均为充电机（SA=0x56）
        TriggerPgn(bms.Rules[0]).Should().Be(0x002600);
        bms.Rules[0].Action.Should().BeOfType<StartMessageAction>()
            .Which.Ref.Should().BeOfType<J1939MessageRef>().Which.Mode.Should().Be(TpMode.Bam);
        TriggerPgn(bms.Rules[1]).Should().Be(0x000100);
        TriggerPgn(bms.Rules[2]).Should().Be(0x000A00);
        TriggerPgn(bms.Rules[3]).Should().Be(0x000A00);
        TriggerPgn(bms.Rules[4]).Should().Be(0x001A00);
        TriggerPgn(bms.Rules[5]).Should().Be(0x001A00);
        TriggerPgn(bms.Rules[6]).Should().Be(0x001A00);
        TriggerPgn(bms.Rules[7]).Should().Be(0x001A00);
        // CRO 启动 BCL(0x1000) + BCS(0x1100)；CST 停二者并回 BST(0x1900)+BSD(0x1C00)
        new[] { bms.Rules[2], bms.Rules[3] }.Select(StartPgn).Should().BeEquivalentTo([0x001000u, 0x001100u]);
        new[] { bms.Rules[4], bms.Rules[5] }.Select(StopPgn).Should().BeEquivalentTo([0x001000u, 0x001100u]);
        // 终审修复（Important）：补 CST→发 BST 规则（镜像充电机模板 BST→发 CST）——此前 BST
        // 报文为孤儿（无规则引用），标准结束序 CST→BST→BSD/CSD 对模拟 BMS 不可达。
        var bst = bms.Rules[6].Action.Should().BeOfType<SendMessageAction>().Which;
        bst.Ref.Should().BeOfType<J1939MessageRef>().Which.Pgn.Should().Be(0x001900);
        ByteCount(bst.Payload).Should().Be(4);
        var bsd = bms.Rules[7].Action.Should().BeOfType<SendMessageAction>().Which;
        bsd.Ref.Should().BeOfType<J1939MessageRef>().Which.Pgn.Should().Be(0x001C00);
        ByteCount(bsd.Payload).Should().Be(7);
        bms.Rules.Select(TriggerSa).Should().OnlyContain(sa => sa == 0x56);
    }

    private static uint TriggerPgn(ResponseRule rule) => ((J1939MessageRef)rule.Trigger).Pgn;

    private static byte? TriggerSa(ResponseRule rule) => ((J1939MessageRef)rule.Trigger).Sa;

    private static uint StartPgn(ResponseRule rule) =>
        rule.Action.Should().BeOfType<StartMessageAction>().Which.Ref
            .Should().BeOfType<J1939MessageRef>().Which.Pgn;

    private static uint StopPgn(ResponseRule rule) =>
        rule.Action.Should().BeOfType<StopMessageAction>().Which.Ref
            .Should().BeOfType<J1939MessageRef>().Which.Pgn;

    private static int ByteCount(NodePayloadSource payload) =>
        payload.Should().BeOfType<FixedHexSource>().Which.Hex.Split(' ').Length;
}
