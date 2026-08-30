using System.Text.Json;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.App.Services.Nodes;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Nodes;

public class NodeModelJsonTests
{
    private static readonly JsonSerializerOptions Opts = NodeConfigLibrary.JsonOptsForTests;

    private static NodeConfig Sample() => new()
    {
        Name = "charger",
        Tag = "gbt27930",
        Identity = new J1939NodeIdentity(0x56),
        Messages = new List<NodeMessage>
        {
            new(new J1939MessageRef(0x002600, 6, TpMode.Single, null, 0xF4), 500,
                new FixedHexSource("01 01 00"), true),
            new(new J1939MessageRef(0x000200, 6, TpMode.Bam, null, 0xFF), 250,
                new DbcSignalsSource("BRM"), false),
        },
        Rules = new List<ResponseRule>
        {
            new(new J1939MessageRef(0x000200, 6, null, 0xF4), new BytePattern(0, 0xFF, 0xAA),
                new StartMessageAction(new J1939MessageRef(0x000200, 6, TpMode.Bam, null, 0xFF)), 10),
            new(new J1939MessageRef(0x001900, 6, null, 0xF4), null,
                new SendMessageAction(new J1939MessageRef(0x001A00, 6, TpMode.Single, null, 0xF4), new FixedHexSource("00 00 00 00")), 0),
            new(new J1939MessageRef(0x001200, 6, null, 0x56), null,
                new SetSignalAction("CCS", "voltage", 400.0), 0),
            new(new J1939MessageRef(0x00F001, 6, null, 0x11), null, new ScriptAction("scripts/loop.js"), 0),
            new(new J1939MessageRef(0x00F002, 6, null, 0x11), null,
                new StopMessageAction(new J1939MessageRef(0x001200, 6, TpMode.Bam, null, 0xFF)), 0),
        },
    };

    [Fact]
    public void RoundTrips_All_Discriminated_Unions()
    {
        var json = JsonSerializer.Serialize(Sample(), Opts);
        json.Should().Contain("\"kind\": \"j1939\"").And.Contain("\"kind\": \"fixedHex\"");

        var restored = JsonSerializer.Deserialize<NodeConfig>(json, Opts);

        restored.Should().BeEquivalentTo(Sample());
    }

    [Fact]
    public void Serializes_Human_Readable_Mode_And_Kind()
    {
        var json = JsonSerializer.Serialize(Sample(), Opts);

        json.Should().Contain("\"Mode\": \"Bam\"");   // JsonStringEnumConverter（缩进 JSON：冒号后带空格；属性名默认 PascalCase）
        json.Should().Contain("\"Pgn\": 9728");        // PGN 十进制序列化（0x002600 = 9728；brief 原文 0x0A00=2560 不在样例中，按实际样例修正）
    }

    [Fact]
    public void FixedHex_Byte_Pattern_Semantics_Are_Preserved()
    {
        var pattern = new BytePattern(0, 0x0F, 0x0A);
        byte[] payload = { 0xAA, 0xBB };

        ((payload[pattern.Offset] & pattern.Mask) == pattern.Value).Should().BeTrue();
    }

    [Fact]
    public void RoundTrips_CanMessageRef_And_Channel_Identity()
    {
        // brief 样例未覆盖 CanMessageRef（"can" 判别符）与非空 Channel —— 补充联合变体全覆盖。
        var config = new NodeConfig
        {
            Name = "raw",
            Identity = new J1939NodeIdentity(0x56) { Channel = "USB1" },
            Messages = [new NodeMessage(new CanMessageRef(0x18FF00F4, true), 100, new FixedHexSource("AA"), true)],
            Rules = [],
        };

        var json = JsonSerializer.Serialize(config, Opts);
        json.Should().Contain("\"kind\": \"can\"");

        var restored = JsonSerializer.Deserialize<NodeConfig>(json, Opts);

        restored.Should().BeEquivalentTo(config);
    }
}
