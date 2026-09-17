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
    public void Reader_ReturnsNull_WhenRootIsNotAnObject()
    {
        // 非对象 root 不得抛裸异常；无 security 语义 → null（suite 完整反序列化处再报错）。
        var path = WriteTemp("json", "[1,2,3]");
        try { SecOcBlockReader.TryRead(path).Should().BeNull(); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_ReturnsNull_WhenNoSecurityField()
    {
        var path = WriteTemp("json", """{"name":"x","cases":[],"globalCaseFixtureKeys":[],"suiteFixtureKeys":[],"config":{}}""");
        try { SecOcBlockReader.TryRead(path).Should().BeNull(); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_FindsSecurityField_CaseInsensitively()
    {
        // 第三方手写 "Security" 不得被静默忽略（否则无保护运行，spec Rev8）。
        var path = WriteTemp("json",
            """{"name":"x","Security":{"pdus":[{"pduName":"TestMsg","canId":{"raw":291,"format":"Standard","type":"Data"},"dataId":1,"fvLenBits":16,"macLenBits":24,"keyId":"KEY_SLOT_05"}]}}""");
        try
        {
            var block = SecOcBlockReader.TryRead(path);
            block.Should().NotBeNull();
            block!.Pdus.Should().HaveCount(1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_ThrowsConsistently_WhenSecurityBlockMalformed()
    {
        // 非对象 security 值：Deserialize 抛 JsonException，须包装为 InvalidOperationException
        // （与 JsonDocument.Parse 失败路径一致，spec Rev8）。
        var path = WriteTemp("json", """{"name":"x","security":42}""");
        try
        {
            var act = () => SecOcBlockReader.TryRead(path);
            act.Should().Throw<InvalidOperationException>().WithMessage("*malformed*");
        }
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
    public void HostDispose_ZeroesSuiteBlockSourceKeyMaterial()
    {
        // SecOcChannel 只克隆密钥；loader 返回给宿主闭包的"源"副本由 DI 的
        // SecOcKeyMaterialZeroizer 在 host 释放时归零（spec D4 / Rev8）。
        var storeDir = Path.Combine(Path.GetTempPath(), $"p4_store_{Guid.NewGuid():N}");
        new DpapiKeyStore(storeDir).SetKey("KEY_SLOT_05", Key);
        var (dbc, suite, ecu) = WriteEcuFixtures(OnePdu);
        try
        {
            var cli = new CliArgs(dbc, suite, EcuScriptPath: ecu, SecOcStoreDir: storeDir);
            var createdBefore = SecOcKeyMaterialZeroizer.InstancesCreated;
            var host = HeadlessHostBuilder.Build(cli);

            // Build 必须已急切实例化归零器（否则生产环境无人解析 → 单例工厂不实例化 →
            // host 释放时源密钥永不归零）。仅靠本测试的显式解析无法守护这一点。
            SecOcKeyMaterialZeroizer.InstancesCreated.Should().Be(createdBefore + 1);

            var zeroizer = host.Services.GetRequiredService<SecOcKeyMaterialZeroizer>();
            zeroizer.AllZeroed.Should().BeFalse("源密钥在 host 释放前仍为明文");

            host.Dispose();

            zeroizer.AllZeroed.Should().BeTrue("host 释放后源密钥副本必须归零");
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
    public void HostBuild_ChannelsPathWithGlobalBlock_AssemblesSecOcChannels(int channelCount)
    {
        // 缺口 1b（2026-09-17）：channels[] + 顶层 security 块不再互斥——顶层块为全局
        // 兜底（每通道回落），MultiChannelAssertionContext 透出默认通道 stats
        // （ISecOcStatsSource + IPerCaseReset），secoc 表达式在 channels[] 路径可用。
        var storeDir = Path.Combine(Path.GetTempPath(), $"p4_store_{Guid.NewGuid():N}");
        new DpapiKeyStore(storeDir).SetKey("KEY_SLOT_05", Key);
        var (dbc, suite, ecu) = WriteEcuFixtures(OnePdu);
        try
        {
            var channels = Enumerable.Range(0, channelCount)
                .Select(i => new PeakCan.HIL.Core.HIL.ChannelConfig($"bus-{i}", "", null, false, null, null, null))
                .ToArray();
            var cli = new CliArgs(dbc, suite, EcuScriptPath: ecu, HardwareChannels: channels, SecOcStoreDir: storeDir);
            using var host = HeadlessHostBuilder.Build(cli);

            var ctx = host.Services.GetRequiredService<global::PeakCan.Host.Core.HIL.Contracts.IAssertionContext>();
            // 顶层块全局兜底 → 每通道被 wrap；上下文透出默认通道 stats。
            ((global::PeakCan.Host.Core.HIL.Contracts.ISecOcStatsSource)ctx).SecOcStats.Should().NotBeNull();
        }
        finally
        {
            File.Delete(dbc); File.Delete(suite); File.Delete(ecu);
            if (Directory.Exists(storeDir)) Directory.Delete(storeDir, recursive: true);
        }
    }

    // ── 缺口 1b：per-channel 块（channels[].security）探取 + 绑定 ────────────────

    [Fact]
    public void Reader_PerChannel_ReturnsEmpty_WhenNoChannels()
    {
        var path = WriteTemp("json", """{"name":"x","cases":[]}""");
        try { SecOcBlockReader.TryReadPerChannel(path).Should().BeEmpty(); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_PerChannel_KeysByChannelName()
    {
        var path = WriteTemp("json", $$"""
            {
              "name": "x",
              "channels": [
                { "name": "bus-a", "handle": "", "baudRate": null, "fd": false,
                  "security": {"pdus":[{"pduName":"T","canId":{"raw":291,"format":"Standard","type":"Data"},"dataId":1,"fvLenBits":16,"macLenBits":24,"keyId":"K"}]} },
                { "name": "bus-b", "handle": "", "baudRate": null, "fd": false, "security": null }
              ],
              "cases": []
            }
            """);
        try
        {
            var blocks = SecOcBlockReader.TryReadPerChannel(path);
            blocks.Keys.Should().Equal("bus-a");
            blocks["bus-a"].Pdus.Should().HaveCount(1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_PerChannel_MalformedBlock_FailsLoud()
    {
        var path = WriteTemp("json", $$"""
            {
              "name": "x",
              "channels": [ { "name": "bus-a", "handle": "", "baudRate": null, "fd": false, "security": 42 } ],
              "cases": []
            }
            """);
        try
        {
            var act = () => SecOcBlockReader.TryReadPerChannel(path);
            act.Should().Throw<InvalidOperationException>().WithMessage("*bus-a*malformed*");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HostBuild_PerChannelSecurity_BindsEachChannel()
    {
        // 缺口 1b：bus-a 有 channel 级块（KEY_SLOT_05）→ wrap；bus-b 无块且无顶层
        // 块 → 回落 --secoc-config（null）→ 裸通道。逐通道绑定各自独立。
        var storeDir = Path.Combine(Path.GetTempPath(), $"p4_store_{Guid.NewGuid():N}");
        new DpapiKeyStore(storeDir).SetKey("KEY_SLOT_05", Key);
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
              "channels": [
                { "name": "bus-a", "handle": "", "baudRate": null, "fd": false, "dbcPath": null, "udsRequestId": null, "udsResponseId": null,
                  "security": { "pdus": [ { "pduName":"TestMsg","canId":{"raw":291,"format":"Standard","type":"Data"},"dataId":1,"fvLenBits":16,"macLenBits":24,"keyId":"KEY_SLOT_05","mode":"both","initialFv":0 } ] } },
                { "name": "bus-b", "handle": "", "baudRate": null, "fd": false, "dbcPath": null, "udsRequestId": null, "udsResponseId": null }
              ],
              "cases": [ { "id": "c1", "name": "d", "steps": [ { "parameters": { "$kind": "delay", "milliseconds": 10 } } ] } ],
              "globalCaseFixtureKeys": [], "suiteFixtureKeys": [],
              "config": { "failurePolicy": "ContinueAll", "continueAfterSetupFailure": true }
            }
            """);
        var ecu = WriteTemp("json", """
            { "name": "P4Ecu", "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" }, "states": [ { "name": "default", "transitions": [] } ] }
            """);
        var channels = new[]
        {
            new PeakCan.HIL.Core.HIL.ChannelConfig("bus-a", "", null, false, null, null, null),
            new PeakCan.HIL.Core.HIL.ChannelConfig("bus-b", "", null, false, null, null, null),
        };
        try
        {
            var cli = new CliArgs(dbc, suite, EcuScriptPath: ecu, HardwareChannels: channels, SecOcStoreDir: storeDir);
            using var host = HeadlessHostBuilder.Build(cli);
            var ctx = (MultiChannelAssertionContext)host.Services
                .GetRequiredService<global::PeakCan.Host.Core.HIL.Contracts.IAssertionContext>();
            ctx.GetChannel("bus-a").Channel.Should().BeOfType<SecOcChannel>();
            ctx.GetChannel("bus-b").Channel.Should().NotBeOfType<SecOcChannel>();
        }
        finally
        {
            File.Delete(dbc); File.Delete(suite); File.Delete(ecu);
            if (Directory.Exists(storeDir)) Directory.Delete(storeDir, recursive: true);
        }
    }
}
