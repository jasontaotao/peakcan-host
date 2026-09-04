using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Nodes;
using PeakCan.Host.App.Tests.Services.Nodes;
using PeakCan.Host.App.ViewModels.Nodes;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Nodes;

/// <summary>
/// Task 18 deferred 的详情编辑器（plan:5645/5661 → spec 决策 1/2）：行 VM 可编辑字段、
/// 增删行、ApplyConfig 组装校验、更新后行集刷新。TDD：编辑契约钉先行。
/// </summary>
public class NodeEditorViewModelTests
{
    private static readonly J1939MessageRef ChmRef = new(0x000100, 6, TpMode.Single, null, 0xF4);
    private static readonly J1939MessageRef BroRef = new(0x000900, 6, null, null, null);
    private static readonly J1939MessageRef CcsRef = new(0x001200, 6, TpMode.Single, null, 0xF4);

    private static NodeEditorViewModel CreateEditor(out NodeHostService host, NodeConfig? config = null)
    {
        host = new NodeHostService((c, r) => new FakeNodeContext(r));
        var editor = new NodeEditorViewModel();
        editor.Bind(host,
            new DbcService(NullLogger<DbcService>.Instance),
            new NodeConfigLibrary(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nodes-{Guid.NewGuid():N}"), null));
        if (config is not null)
            host.AddNode(config);
        editor.Select(config, running: false);
        return editor;
    }

    private static NodeConfig ConfigWithTwoMessagesAndRule() => new()
    {
        Name = "chg",
        Identity = new J1939NodeIdentity(0x11),
        Messages =
        [
            new NodeMessage(ChmRef, 250, new FixedHexSource("AA 00"), true),           // 单帧 2B
            new NodeMessage(CcsRef, 50, new FixedHexSource("A0 0F 88"), false),        // 单帧 3B，禁用
        ],
        Rules =
        [
            new ResponseRule(BroRef, null, new StartMessageAction(CcsRef), 0),
        ],
    };

    [Fact]
    public void Select_Populates_Editable_Rows_From_Config()
    {
        var editor = CreateEditor(out _, ConfigWithTwoMessagesAndRule());

        editor.Messages.Should().HaveCount(2);
        var m0 = editor.Messages[0];
        m0.PgnHex.Should().Be("100");              // 0x000100 → "100"
        m0.ModeIndex.Should().Be(0);               // Single
        m0.Enabled.Should().BeTrue();
        m0.PayloadKindIndex.Should().Be(0);        // FixedHex
        m0.PayloadHexText.Should().Be("AA 00");

        var m1 = editor.Messages[1];
        m1.Enabled.Should().BeFalse();             // 行状态双写（Enabled 装载）

        var r0 = editor.Rules.Single();
        r0.TriggerPgnHex.Should().Be("900");
        r0.ActionKindIndex.Should().Be(2);         // start
        r0.ActionRefPgnHex.Should().Be("1200");
    }

    [Fact]
    public void AddMessage_Appends_Editable_Row()
    {
        var editor = CreateEditor(out _, ConfigWithTwoMessagesAndRule());
        var before = editor.Messages.Count;

        editor.AddMessageCommand.Execute(null);

        editor.Messages.Should().HaveCount(before + 1);
        var fresh = editor.Messages[^1];
        fresh.PgnHex.Should().BeEmpty();           // 新行默认空，等编辑
        fresh.PayloadKindIndex.Should().Be(0);
        fresh.Enabled.Should().BeTrue();
    }

    [Fact]
    public void DeleteMessage_Removes_Selected_Row()
    {
        var editor = CreateEditor(out _, ConfigWithTwoMessagesAndRule());
        editor.SelectedMessage = editor.Messages[0];

        editor.DeleteMessageCommand.Execute(null);

        editor.Messages.Single().PgnHex.Should().Be("1200");   // 首行被删，剩 CCS
        editor.SelectedMessage.Should().BeNull();              // 选中收敛
    }

    [Fact]
    public void Selected_Message_And_Rule_Are_Mutually_Exclusive()
    {
        var editor = CreateEditor(out _, ConfigWithTwoMessagesAndRule());

        editor.SelectedMessage = editor.Messages[0];
        editor.SelectedRule = editor.Rules[0];

        editor.SelectedMessage.Should().BeNull();   // 选规则清消息
    }

    [Fact]
    public void ApplyConfig_On_Unchanged_Rows_Is_Equivalent_To_Original()
    {
        var editor = CreateEditor(out var host, ConfigWithTwoMessagesAndRule());

        var assembled = editor.AssembleConfig(out var error);
        Assert.NotNull(assembled);
        var applied = host.UpdateNode("chg", assembled);

        applied.IsSuccess.Should().BeTrue();
        var cfg = host.Nodes.Single().Config;
        cfg.Messages.Should().HaveCount(2);
        cfg.Messages[0].Ref.Should().Be(ChmRef);
        cfg.Messages[0].IntervalMs.Should().Be(250);
        cfg.Messages[0].Payload.Should().Be(new FixedHexSource("AA 00"));
        cfg.Messages[1].Enabled.Should().BeFalse();            // 行状态回写
        cfg.Rules.Single().Action.Should().Be(new StartMessageAction(CcsRef));
    }

    [Fact]
    public void ApplyConfig_With_FixedHex_Edit_Produces_New_Config()
    {
        var editor = CreateEditor(out var host, ConfigWithTwoMessagesAndRule());

        editor.Messages[0].IntervalMsText = "500";
        editor.Messages[0].PayloadHexText = "AA BB CC";
        var config = editor.AssembleConfig(out var error);
        Assert.NotNull(config);

        error.Should().BeNull();
        var msg = config.Messages[0];
        msg.IntervalMs.Should().Be(500);
        msg.Payload.Should().Be(new FixedHexSource("AA BB CC"));
        host.Nodes.Single().Config.Should().NotBeSameAs(config);   // 组装是新 record（还没 UpdateNode）
    }

    [Fact]
    public void ApplyConfig_Rejects_Single_With_More_Than_8_Bytes()
    {
        var editor = CreateEditor(out _, ConfigWithTwoMessagesAndRule());
        // 单帧消息给 9 字节载荷（"AA " * 9）→ 必须拒绝（J1939SendViewModel 同款："≤8 字节请选择单帧模式" 的反面）
        editor.Messages[0].PayloadHexText = string.Join(" ", Enumerable.Repeat("AA", 9));

        var config = editor.AssembleConfig(out var error);

        config.Should().BeNull();
        error.Should().Contain("单帧");
    }

    [Fact]
    public void ApplyConfig_Rejects_Empty_Name_And_Invalid_Sa()
    {
        var editor = CreateEditor(out _, ConfigWithTwoMessagesAndRule());

        editor.NodeName = "";
        editor.AssembleConfig(out var nameError).Should().BeNull();
        nameError.Should().Contain("名称");

        editor.NodeName = "chg";
        editor.NodeSaHex = "ZZ";
        editor.AssembleConfig(out var saError).Should().BeNull();
        saError.Should().Contain("SA");
    }

    [Fact]
    public void ApplyConfig_RoundTrips_Dbc_And_Script_Payload_Kinds()
    {
        var editor = CreateEditor(out _, ConfigWithTwoMessagesAndRule());
        var m1 = editor.Messages[1];
        m1.PayloadKindIndex = 1;                       // DbcSignals
        m1.PayloadDbcMessageName = "BCL";
        var m0 = editor.Messages[0];
        m0.PayloadKindIndex = 2;                       // Script（plan 修订 10：编辑支持，运行时报错）
        m0.PayloadScriptRefText = "custom.doSomething";

        var config = editor.AssembleConfig(out var error);
        Assert.NotNull(config);

        error.Should().BeNull();
        config.Messages[0].Payload.Should().Be(new ScriptCallbackSource("custom.doSomething"));
        config.Messages[1].Payload.Should().Be(new DbcSignalsSource("BCL"));
    }

    [Fact]
    public void ApplyConfig_Command_Commits_To_Host_And_Fires_ConfigApplied()
    {
        var editor = CreateEditor(out var host, ConfigWithTwoMessagesAndRule());
        NodeConfig? committed = null;
        editor.ConfigApplied += c => committed = c;

        editor.NodeSaHex = "22";
        editor.Messages[0].PayloadHexText = "AA BB";
        editor.ApplyConfigCommand.Execute(null);

        committed.Should().NotBeNull();
        host.Nodes.Single().Config.Identity.Should().Be(new J1939NodeIdentity(0x22));
        host.Nodes.Single().Config.Messages[0].Payload.Should().Be(new FixedHexSource("AA BB"));
        editor.ApplyStatus.Should().Contain("已应用");
        editor.Messages[0].PayloadHexText.Should().Be("AA BB");   // 行集重建后编辑值保留（经装载）
    }

    [Fact]
    public void ApplyConfig_While_Running_Is_Rejected()
    {
        var host = new NodeHostService((c, r) => new FakeNodeContext(r));
        var editor = new NodeEditorViewModel();
        editor.Bind(host, new DbcService(NullLogger<DbcService>.Instance),
            new NodeConfigLibrary(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nodes-{Guid.NewGuid():N}"), null));
        host.AddNode(ConfigWithTwoMessagesAndRule());
        editor.Select(host.Nodes.Single().Config, running: true);   // 模拟运行中（门语义）

        editor.ApplyConfigCommand.Execute(null);

        editor.ApplyStatus.Should().Contain("运行中");
        host.Nodes.Single().Config.Name.Should().Be("chg");   // 未生效
    }

    // review 修复钉：编辑区不呈现的字段（Tag / AddressClaimEnabled）从原配置透传——
    // 否则 ApplyConfig 静默抹掉分组与地址声明行为（review 2×MEDIUM）。
    [Fact]
    public void ApplyConfig_Preserves_Tag_And_AddressClaimFrom_Original()
    {
        var host = new NodeHostService((c, r) => new FakeNodeContext(r));
        var editor = new NodeEditorViewModel();
        editor.Bind(host, new DbcService(NullLogger<DbcService>.Instance),
            new NodeConfigLibrary(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nodes-{Guid.NewGuid():N}"), null));
        var config = ConfigWithTwoMessagesAndRule() with { Tag = "gbt27930", AddressClaimEnabled = true };
        host.AddNode(config);
        editor.Select(config, running: false);

        editor.ApplyConfigCommand.Execute(null);

        var applied = host.Nodes.Single().Config;
        applied.Tag.Should().Be("gbt27930");           // StartAll(tag) 分组不丢
        applied.AddressClaimEnabled.Should().BeTrue();
    }

    // review 修复钉：RTS-CTS 无 DA 在组装期拒绝（SendViewModel 同款入口校验——
    // 否则 SendCore 每周期被拒、活动流噪音）。
    [Fact]
    public void ApplyConfig_Rejects_RtsCts_Without_Da()
    {
        var editor = CreateEditor(out _, ConfigWithTwoMessagesAndRule());
        var m = editor.Messages[0];
        m.ModeIndex = 2;                 // RTS-CTS
        m.DaHex = "";

        var config = editor.AssembleConfig(out var error);

        config.Should().BeNull();
        error.Should().Contain("DA");
    }

    [Fact]
    public void Assembled_Config_Json_RoundTrips()
    {
        // 编辑闭环钉：组装产物落盘（NodeConfigLibrary.Save 路径的 JSON 契约）→ 回读等价。
        var editor = CreateEditor(out _, ConfigWithTwoMessagesAndRule());
        editor.Messages[0].PayloadKindIndex = 2;             // 脚本载荷改写后再落盘
        editor.Messages[0].PayloadScriptRefText = "ccs.follow";
        var config = editor.AssembleConfig(out var error);
        Assert.NotNull(config);

        var json = System.Text.Json.JsonSerializer.Serialize(config, NodeConfigLibrary.JsonOptsForTests);
        var restored = System.Text.Json.JsonSerializer.Deserialize<NodeConfig>(json, NodeConfigLibrary.JsonOptsForTests);

        restored.Should().BeEquivalentTo(config);
    }

    [Fact]
    public void ApplyConfig_With_Rule_Script_Action_Is_Editable()
    {
        var editor = CreateEditor(out _, ConfigWithTwoMessagesAndRule());
        var rule = editor.Rules.Single();
        rule.ActionKindIndex = 4;                      // script（编辑支持契约）
        rule.ActionScriptRefText = "onBro";

        var config = editor.AssembleConfig(out var error);
        Assert.NotNull(config);

        error.Should().BeNull();
        config.Rules.Single().Action.Should().Be(new ScriptAction("onBro"));
    }
}