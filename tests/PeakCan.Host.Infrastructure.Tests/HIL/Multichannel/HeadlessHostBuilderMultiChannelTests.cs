using Microsoft.Extensions.DependencyInjection;
using PeakCan.HIL.Core;
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
}
