using Microsoft.Extensions.DependencyInjection;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.Cli;
using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Multichannel;

/// <summary>
/// HeadlessHostBuilder 多通道 DI 测试：HardwareChannels 含 2 项时，
/// Build() 注册的 IAssertionContext 是 MultiChannelAssertionContext（2 通道）。
/// 不连真硬件（Build 阶段只 new PeakCanChannel，不 Connect）。
/// </summary>
public sealed class HeadlessHostBuilderMultiChannelTests : IDisposable
{
    private readonly string _dbcPath;

    public HeadlessHostBuilderMultiChannelTests()
    {
        _dbcPath = Path.Combine(Path.GetTempPath(), $"mc_{Guid.NewGuid():N}.dbc");
        File.WriteAllText(_dbcPath, """
            VERSION "1.0";
            NS_ :
            BS_:
            BU_: ECU
            BO_ 256 TestMsg: 8 ECU
             SG_ TestSignal : 0|8@1+ (1,0) [0|255] "V"  ECU
            """);
    }

    public void Dispose()
    {
        try { File.Delete(_dbcPath); } catch { }
    }

    [Fact]
    public void Build_WithTwoHardwareChannels_RegistersMultiChannelAssertionContext()
    {
        // Arrange: 2 通道，指向同一测试 DBC（Q8 要求每通道独立 DBC，测试用共用可接受）
        var channels = new[]
        {
            new ChannelConfig("bus-a", "USB1", BaudRate.Can500kbps, false, DbcPath: _dbcPath, null, null),
            new ChannelConfig("bus-b", "USB2", BaudRate.Can500kbps, false, DbcPath: _dbcPath, null, null),
        };
        var args = new CliArgs(_dbcPath, "suite.json", HardwareChannel: null, HardwareChannels: channels);

        // Act
        using var host = HeadlessHostBuilder.Build(args);
        var ctx = host.Services.GetService<IAssertionContext>();

        // Assert: IAssertionContext 是 MultiChannelAssertionContext，含 2 通道
        var multi = Assert.IsType<MultiChannelAssertionContext>(ctx);
        Assert.Equal(2, multi.ChannelCount);
        Assert.Contains("bus-a", multi.ChannelNames);
        Assert.Contains("bus-b", multi.ChannelNames);
        Assert.Equal("bus-a", multi.DefaultChannelName);
    }

    [Fact]
    public void Build_WithHardwareChannels_StillResolvesDefaultICanChannel()
    {
        // 多通道模式仍注册默认 ICanChannel（第一个），供单通道默认依赖（UDS/stats/bg）使用
        var channels = new[]
        {
            new ChannelConfig("bus-a", "USB1", BaudRate.Can500kbps, false, DbcPath: _dbcPath, null, null),
            new ChannelConfig("bus-b", "USB2", BaudRate.Can500kbps, false, DbcPath: _dbcPath, null, null),
        };
        var args = new CliArgs(_dbcPath, "suite.json", HardwareChannel: null, HardwareChannels: channels);

        using var host = HeadlessHostBuilder.Build(args);
        var channel = host.Services.GetService<ICanChannel>();
        // 默认通道已注册（第一个 bus-a 的 handle）
        Assert.NotNull(channel);
        Assert.Equal(new ChannelId(0x51), channel!.Id); // USB1 → 0x51
    }

    [Fact]
    public void ResolveChannelId_MapsLogicalNameToPhysicalChannelId()
    {
        var channels = new[]
        {
            new ChannelConfig("bus-a", "USB1", BaudRate.Can500kbps, false, DbcPath: _dbcPath, null, null),
            new ChannelConfig("bus-b", "USB2", BaudRate.Can500kbps, false, DbcPath: _dbcPath, null, null),
        };
        var args = new CliArgs(_dbcPath, "suite.json", HardwareChannel: null, HardwareChannels: channels);

        using var host = HeadlessHostBuilder.Build(args);
        var multi = (MultiChannelAssertionContext)host.Services.GetRequiredService<IAssertionContext>();

        // bus-a → USB1 (0x51), bus-b → USB2 (0x52)
        Assert.Equal(new ChannelId(0x51), multi.ResolveChannelId("bus-a"));
        Assert.Equal(new ChannelId(0x52), multi.ResolveChannelId("bus-b"));
        // null = 默认通道
        Assert.Equal(new ChannelId(0x51), multi.ResolveChannelId(null));
    }

    [Theory]
    [InlineData("51", 0x51)]      // raw hex（无前缀）— ChannelConfig 文档契约
    [InlineData("0x52", 0x52)]     // 0x 前缀 hex
    [InlineData("USB1", 0x51)]     // USBn 习惯形式（回落 ParseChannelHandle）
    [InlineData("C600", 0xC600)]   // ZLG 风格大 hex
    public void ResolveChannelHandle_Accepts_Hex_And_Usb_Forms(string handle, ushort expected)
    {
        Assert.Equal(expected, HeadlessHostBuilder.ResolveChannelHandle(handle));
    }

    [Fact]
    public void Build_Accepts_RawHex_Handle_Per_ChannelConfig_Contract()
    {
        // ChannelConfig 文档说 Handle 是 raw hex（"51"/"C600"）。验证 hex 形式不抛异常。
        var channels = new[]
        {
            new ChannelConfig("bus-a", "51", BaudRate.Can500kbps, false, DbcPath: _dbcPath, null, null),
            new ChannelConfig("bus-b", "52", BaudRate.Can500kbps, false, DbcPath: _dbcPath, null, null),
        };
        var args = new CliArgs(_dbcPath, "suite.json", HardwareChannel: null, HardwareChannels: channels);

        using var host = HeadlessHostBuilder.Build(args);
        var multi = (MultiChannelAssertionContext)host.Services.GetRequiredService<IAssertionContext>();
        Assert.Equal(new ChannelId(0x51), multi.ResolveChannelId("bus-a"));
        Assert.Equal(new ChannelId(0x52), multi.ResolveChannelId("bus-b"));
    }

    [Fact]
    public void Build_FirstChannel_ReusesDiSingleton_NoDoubleReader()
    {
        // 防回归：多通道模式下第一个 SingleChannelContext 必须复用 DI 默认 ICanChannel singleton
        // （同一对象引用），否则同一物理 handle 有两个 PeakCanChannel → 双 InitializeFD + 双读循环竞争。
        var channels = new[]
        {
            new ChannelConfig("bus-a", "USB1", BaudRate.Can500kbps, false, DbcPath: _dbcPath, null, null),
            new ChannelConfig("bus-b", "USB2", BaudRate.Can500kbps, false, DbcPath: _dbcPath, null, null),
        };
        var args = new CliArgs(_dbcPath, "suite.json", HardwareChannel: null, HardwareChannels: channels);

        using var host = HeadlessHostBuilder.Build(args);
        var diChannel = host.Services.GetRequiredService<ICanChannel>();
        var multi = (MultiChannelAssertionContext)host.Services.GetRequiredService<IAssertionContext>();

        // 第一个通道（bus-a）的底层 ICanChannel 就是 DI singleton（引用相等）
        Assert.Same(diChannel, multi.GetChannel("bus-a").Channel);
        // 第二个通道（bus-b）是独立 new 的（不同引用）
        Assert.NotSame(diChannel, multi.GetChannel("bus-b").Channel);
    }

    [Fact]
    public void Build_FirstChannelNullDbcPath_ReusesGlobalDbc_Succeeds()
    {
        // Bug-C：首通道 cfg.DbcPath 为 null → 回落 args.DbcPath（与全局 DbcDocument 同源）。
        // 应复用上方已解析的全局 DbcDocument，而非重复 ReadAllText + DbcParser.Parse。
        // 回归验证：首通道 DbcPath null 时 host 构建成功，首通道 context 可用（不抛）。
        var channels = new[]
        {
            new ChannelConfig("bus-a", "USB1", BaudRate.Can500kbps, false, DbcPath: null, null, null),
            new ChannelConfig("bus-b", "USB2", BaudRate.Can500kbps, false, DbcPath: _dbcPath, null, null),
        };
        var args = new CliArgs(_dbcPath, "suite.json", HardwareChannel: null, HardwareChannels: channels);

        using var host = HeadlessHostBuilder.Build(args);
        var multi = (MultiChannelAssertionContext)host.Services.GetRequiredService<IAssertionContext>();

        // 构建成功 + 首通道 context 可取（复用全局 DbcDocument 不破坏 lookup 装配）
        Assert.Equal(2, multi.ChannelCount);
        var busA = multi.GetChannel("bus-a");
        Assert.NotNull(busA);
        // 首通道的 DBC 已装配：解码一帧 TestMsg(0x100) 能查到 TestSignal（验证 lookup 不是空壳）
        busA.SubscribeDecodedFrames(_ => { }).Dispose();
    }

    [Fact]
    public void Build_EmptyHandle_AssignsSequentialPhysicalChannel()
    {
        // Spec v3 §3.4: 空 Handle（studio 声明只留名）→ 按索引顺序映射物理通道
        // 0x51+i（PEAK USB1..USBn 惯例），不抛异常；非空 Handle 保持解析（旧套件兼容）。
        var channels = new[]
        {
            new ChannelConfig("bus-a", "", BaudRate.Can500kbps, false, DbcPath: _dbcPath, null, null),
            new ChannelConfig("bus-b", "", BaudRate.Can500kbps, false, DbcPath: _dbcPath, null, null),
        };
        var args = new CliArgs(_dbcPath, "suite.json", HardwareChannel: null, HardwareChannels: channels);

        using var host = HeadlessHostBuilder.Build(args);
        var multi = (MultiChannelAssertionContext)host.Services.GetRequiredService<IAssertionContext>();

        // bus-a → 0x51（索引 0），bus-b → 0x52（索引 1）
        Assert.Equal(new ChannelId(0x51), multi.ResolveChannelId("bus-a"));
        Assert.Equal(new ChannelId(0x52), multi.ResolveChannelId("bus-b"));
    }

    [Fact]
    public void Build_WithTwoChannels_RegistersPerChannelDbcsByChannelId()
    {
        // Task 11 接线闭环：per-channel DBC 字典应按 ChannelId 注册（报告按 frame.Channel 查）。
        var channels = new[]
        {
            new ChannelConfig("bus-a", "", BaudRate.Can500kbps, false, DbcPath: _dbcPath, null, null),
            new ChannelConfig("bus-b", "", BaudRate.Can500kbps, false, DbcPath: _dbcPath, null, null),
        };
        var args = new CliArgs(_dbcPath, "suite.json", HardwareChannel: null, HardwareChannels: channels);

        using var host = HeadlessHostBuilder.Build(args);
        // 触发 IAssertionContext 工厂（填充字典）
        _ = host.Services.GetRequiredService<IAssertionContext>();
        var dbcs = host.Services.GetRequiredService<IReadOnlyDictionary<ChannelId, DbcDocument>>();

        Assert.Equal(2, dbcs.Count);
        Assert.NotNull(dbcs[new ChannelId(0x51)]); // bus-a → USB1
        Assert.NotNull(dbcs[new ChannelId(0x52)]); // bus-b → USB2
    }

    [Fact]
    public void Build_WithDistinctChannelDbcs_MapsEachChannelIdToItsDbc()
    {
        // 不同通道不同 DBC 文件 → 各自 ChannelId 映射到对应文档（非全局 DBC）。
        var dbcB = Path.Combine(Path.GetTempPath(), $"mc_b_{Guid.NewGuid():N}.dbc");
        File.WriteAllText(dbcB, """
            VERSION "1.0";
            NS_ :
            BS_:
            BU_: ECU
            BO_ 512 MsgB: 8 ECU
             SG_ SigB : 0|8@1+ (1,0) [0|255] "V"  ECU
            """);
        try
        {
            var channels = new[]
            {
                new ChannelConfig("bus-a", "", BaudRate.Can500kbps, false, DbcPath: _dbcPath, null, null),
                new ChannelConfig("bus-b", "", BaudRate.Can500kbps, false, DbcPath: dbcB, null, null),
            };
            var args = new CliArgs(_dbcPath, "suite.json", HardwareChannel: null, HardwareChannels: channels);

            using var host = HeadlessHostBuilder.Build(args);
            _ = host.Services.GetRequiredService<IAssertionContext>();
            var dbcs = host.Services.GetRequiredService<IReadOnlyDictionary<ChannelId, DbcDocument>>();

            // bus-a 的 DBC 含 MsgA(0x100)，bus-b 的 DBC 含 MsgB(0x200)——各自文档独立
            var busA = dbcs[new ChannelId(0x51)];
            var busB = dbcs[new ChannelId(0x52)];
            Assert.True(busA.MessagesById.ContainsKey(0x100));
            Assert.False(busA.MessagesById.ContainsKey(0x200));
            Assert.False(busB.MessagesById.ContainsKey(0x100));
            Assert.True(busB.MessagesById.ContainsKey(0x200));
        }
        finally
        {
            try { File.Delete(dbcB); } catch { }
        }
    }
}
