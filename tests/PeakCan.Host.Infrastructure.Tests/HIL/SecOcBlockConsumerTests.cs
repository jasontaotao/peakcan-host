using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Security;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel.SecOc;
using PeakCan.Host.Infrastructure.Cli;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Security.Keystore;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

/// <summary>
/// Phase 4（spec 2026-09-07-secoc-0x27 §8）：host 消费 suite 内嵌 SecOC security 块。
/// 覆盖块解析、keyId 解析 fail-loud、结构校验，以及块优先于 --secoc-config 的组装语义。
/// </summary>
public class SecOcBlockConsumerTests
{
    private static readonly byte[] Key = Convert.FromHexString("00112233445566778899aabbccddeeff");

    private static SecOcPduDefinition Pdu(
        uint raw = 0x123, ushort dataId = 1, string keyId = "KEY_SLOT_05", string mode = "both",
        uint initialFv = 0)
        => new()
        {
            PduName = "TestMsg",
            CanId = new CanId(raw, FrameFormat.Standard),
            DataId = dataId,
            FvLenBits = 16,
            MacLenBits = 24,
            KeyId = keyId,
            Mode = mode,
            InitialFv = initialFv,
        };

    private static string WriteTemp(string ext, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"p4_{Guid.NewGuid():N}.{ext}");
        File.WriteAllText(path, content);
        return path;
    }

    // ── LoadFromBlock (unit) ──────────────────────────────────────────────────

    [Fact]
    public void LoadFromBlock_BuildsConfig_KeyedByRaw()
    {
        var store = new InMemoryKeyStore();
        store.SetKey("KEY_SLOT_05", Key);
        var block = new SecOcBlock([Pdu(initialFv: 7)]);

        var configs = SecOcConfigLoader.LoadFromBlock(block, store);

        configs.Should().ContainKey(0x123u);
        var cfg = configs[0x123u];
        cfg.Profile.DataId.Should().Be(1);
        cfg.Profile.FvLenBits.Should().Be(16);
        cfg.Profile.MacLenBits.Should().Be(24);
        cfg.Mode.Should().Be(SecOcPduMode.Both);
        cfg.InitialFv.Should().Be(7);
        cfg.Key.Should().Equal(Key);
    }

    [Fact]
    public void LoadFromBlock_ThrowsOnMissingKey()
    {
        var store = new InMemoryKeyStore();
        var block = new SecOcBlock([Pdu(keyId: "MISSING")]);

        var act = () => SecOcConfigLoader.LoadFromBlock(block, store);

        act.Should().Throw<InvalidOperationException>().WithMessage("*MISSING*KeyStore*");
    }

    [Fact]
    public void LoadFromBlock_ThrowsOnStructurallyInvalidBlock()
    {
        var store = new InMemoryKeyStore();
        store.SetKey("KEY_SLOT_05", Key);
        // 同一 CAN Raw + 不同 DataId → 结构校验（重复 CAN id）应拦截。
        var block = new SecOcBlock([Pdu(), Pdu(dataId: 2)]);

        var act = () => SecOcConfigLoader.LoadFromBlock(block, store);

        act.Should().Throw<InvalidOperationException>().WithMessage("*invalid*");
    }

    // ── SecOcBlockReader (unit) ───────────────────────────────────────────────

    [Fact]
    public void Reader_ReturnsNull_WhenPathMissing()
        => SecOcBlockReader.TryRead(null).Should().BeNull();

    [Fact]
    public void Reader_ReturnsNull_WhenNoSecurityField()
    {
        var path = WriteTemp("json", """{"name":"x","cases":[],"globalCaseFixtureKeys":[],"suiteFixtureKeys":[],"config":{}}""");
        try { SecOcBlockReader.TryRead(path).Should().BeNull(); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_ReturnsNull_WhenSecurityExplicitlyNull()
    {
        var path = WriteTemp("json", """{"name":"x","security":null}""");
        try { SecOcBlockReader.TryRead(path).Should().BeNull(); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_ReadsSecurityBlock()
    {
        var path = WriteTemp("json",
            """{"name":"x","security":{"pdus":[{"pduName":"TestMsg","canId":{"raw":291,"format":"Standard","type":"Data"},"dataId":1,"fvLenBits":16,"macLenBits":24,"keyId":"KEY_SLOT_05"}]}}""");
        try
        {
            var block = SecOcBlockReader.TryRead(path);
            block.Should().NotBeNull();
            block!.Pdus.Should().HaveCount(1);
            block.Pdus![0].CanId.Raw.Should().Be(0x123);
            block.Pdus[0].KeyId.Should().Be("KEY_SLOT_05");
        }
        finally { File.Delete(path); }
    }

    // ── Host assembly: suite block precedence + fail-loud ─────────────────────

    private static (string dbc, string suite, string ecu) WriteEcuFixtures(string securityJson)
    {
        var dbc = WriteTemp("dbc", """
            VERSION "1.0";
            NS_ :
            BS_:
            BU_: ECU
            BO_ 256 TestMsg: 8 ECU
             SG_ TestSignal : 0|8@1+ (1,0) [0|255] "V"  ECU
            """);
        var suite = WriteTemp("json", $$"""
            {
              "name": "P4Suite",
              "cases": [ { "id": "c1", "name": "d", "steps": [ { "parameters": { "$kind": "delay", "milliseconds": 10 } } ] } ],
              "globalCaseFixtureKeys": [],
              "suiteFixtureKeys": [],
              "config": { "failurePolicy": "ContinueAll", "continueAfterSetupFailure": true },
              "security": {{securityJson}}
            }
            """);
        var ecu = WriteTemp("json", """
            { "name": "P4Ecu", "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" }, "states": [ { "name": "default", "transitions": [] } ] }
            """);
        return (dbc, suite, ecu);
    }

    private const string OnePdu = """
        {"pdus":[{"pduName":"TestMsg","canId":{"raw":291,"format":"Standard","type":"Data"},"dataId":1,"fvLenBits":16,"macLenBits":24,"keyId":"KEY_SLOT_05","mode":"both","initialFv":0}]}
        """;

    [Fact]
    public void HostBuild_SuiteBlock_WrapsChannel_AndPrefersOverConfigPath()
    {
        var storeDir = Path.Combine(Path.GetTempPath(), $"p4_store_{Guid.NewGuid():N}");
        new DpapiKeyStore(storeDir).SetKey("KEY_SLOT_05", Key);
        var (dbc, suite, ecu) = WriteEcuFixtures(OnePdu);
        try
        {
            // SecOcConfigPath 指向不存在的文件：若回落到 --secoc-config 会抛 FileNotFound；
            // 块优先则不触碰它 → 证明优先级。
            var cli = new CliArgs(dbc, suite, EcuScriptPath: ecu,
                SecOcStoreDir: storeDir, SecOcConfigPath: @"C:\definitely\missing\secoc-config.json");
            using var host = HeadlessHostBuilder.Build(cli);

            host.Services.GetRequiredService<ICanChannel>().Should().BeAssignableTo<ISecureChannel>()
                .And.BeOfType<SecOcChannel>();
        }
        finally
        {
            File.Delete(dbc); File.Delete(suite); File.Delete(ecu);
            if (Directory.Exists(storeDir)) Directory.Delete(storeDir, recursive: true);
        }
    }

    [Fact]
    public void HostBuild_SuiteBlockMissingKey_FailsLoud()
    {
        var storeDir = Path.Combine(Path.GetTempPath(), $"p4_store_{Guid.NewGuid():N}");
        Directory.CreateDirectory(storeDir); // 空 store
        var (dbc, suite, ecu) = WriteEcuFixtures(OnePdu);
        try
        {
            var cli = new CliArgs(dbc, suite, EcuScriptPath: ecu, SecOcStoreDir: storeDir);
            var act = () => HeadlessHostBuilder.Build(cli);
            act.Should().Throw<InvalidOperationException>().WithMessage("*KEY_SLOT_05*");
        }
        finally
        {
            File.Delete(dbc); File.Delete(suite); File.Delete(ecu);
            if (Directory.Exists(storeDir)) Directory.Delete(storeDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void HostBuild_ChannelsPathWithSuiteBlock_FailsLoud(int channelCount)
    {
        // SecOC 块 v1 仅支持传统单通道模式；channels[] 路径（含单个声明通道）一律走
        // MultiChannelAssertionContext，不透出 SecOC 统计 / 每-case 复位 —— 必须显式拒绝，
        // 而非静默禁用 secocRejected（否则攻击断言会查询未知函数）。
        var (dbc, suite, ecu) = WriteEcuFixtures(OnePdu);
        try
        {
            var channels = Enumerable.Range(0, channelCount)
                .Select(i => new PeakCan.HIL.Core.HIL.ChannelConfig($"bus-{i}", "", null, false, null, null, null))
                .ToArray();
            var cli = new CliArgs(dbc, suite, EcuScriptPath: ecu, HardwareChannels: channels);
            var act = () => HeadlessHostBuilder.Build(cli);
            act.Should().Throw<InvalidOperationException>().WithMessage("*not supported on the channels*");
        }
        finally
        {
            File.Delete(dbc); File.Delete(suite); File.Delete(ecu);
        }
    }
}
