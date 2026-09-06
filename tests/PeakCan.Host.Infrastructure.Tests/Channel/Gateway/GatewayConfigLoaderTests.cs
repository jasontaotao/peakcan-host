using PeakCan.Host.Core.HIL.Gateway;
using PeakCan.Host.Infrastructure.Channel.Gateway;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Channel.Gateway;

/// <summary>
/// GatewayConfigLoader unit tests — JSON 解析（小驼峰键）+ 校验（通道格式/Min≤Max/MapToCanId 范围）。
/// </summary>
public sealed class GatewayConfigLoaderTests
{
    private const string ValidJson = """
        {
          "targetChannel": "USB2",
          "bidirectional": true,
          "minCanId": 4096,
          "maxCanId": 8191,
          "mapToCanId": 512
        }
        """;

    [Fact]
    public void Parse_ValidJson_ReturnsConfig()
    {
        var config = GatewayConfigLoader.Parse(ValidJson);

        Assert.Equal("USB2", config.TargetChannel);
        Assert.True(config.Bidirectional);
        Assert.Equal(4096u, config.MinCanId);
        Assert.Equal(8191u, config.MaxCanId);
        Assert.Equal(512u, config.MapToCanId);
    }

    [Fact]
    public void Parse_DefaultValues()
    {
        var config = GatewayConfigLoader.Parse("""{ "targetChannel": "USB2" }""");

        Assert.False(config.Bidirectional);
        Assert.Null(config.MinCanId);
        Assert.Null(config.MaxCanId);
        Assert.Null(config.MapToCanId);
    }

    [Fact]
    public void Parse_TargetMissing_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => GatewayConfigLoader.Parse("""{ "bidirectional": true }"""));
        Assert.Contains("TargetChannel", ex.Message);
    }

    [Theory]
    [InlineData("\"USB17\"")]   // > 16
    [InlineData("\"USB0\"")]    // < 1
    [InlineData("\"USBX\"")]    // 非数字
    [InlineData("\"COM1\"")]    // 非 USB
    [InlineData("\"\"")]
    public void Parse_InvalidChannel_Throws(string channelJson)
    {
        var json = $$"""{ "targetChannel": {{channelJson}} }""";
        var ex = Assert.Throws<ArgumentException>(() => GatewayConfigLoader.Parse(json));
        Assert.Contains("TargetChannel", ex.Message);
        Assert.Contains("invalid", ex.Message);
    }

    [Fact]
    public void Parse_LowercaseUsb_IsValid()
    {
        // ValidateChannelName 用 OrdinalIgnoreCase —— "usb2" 合法（与 ParseChannelHandle 语义一致）。
        var config = GatewayConfigLoader.Parse("""{ "targetChannel": "usb2" }""");
        Assert.Equal("usb2", config.TargetChannel);
    }

    [Fact]
    public void Parse_MinGreaterThanMax_Throws()
    {
        var json = """{ "targetChannel": "USB2", "minCanId": 100, "maxCanId": 50 }""";
        var ex = Assert.Throws<ArgumentException>(() => GatewayConfigLoader.Parse(json));
        Assert.Contains("MinCanId", ex.Message);
    }

    [Fact]
    public void Parse_MapToCanIdOverflow_Throws()
    {
        // 0x20000000 > 0x1FFFFFFF（29 位 CAN ID 上限）
        var json = """{ "targetChannel": "USB2", "mapToCanId": 536870912 }""";
        var ex = Assert.Throws<ArgumentException>(() => GatewayConfigLoader.Parse(json));
        Assert.Contains("MapToCanId", ex.Message);
    }

    [Fact]
    public void Parse_MinCanIdOverflow_Throws()
    {
        // L1: 过滤值越界为静默错误配置（过滤条件恒真）—— 必须校验。
        var json = """{ "targetChannel": "USB2", "minCanId": 536870912 }""";
        var ex = Assert.Throws<ArgumentException>(() => GatewayConfigLoader.Parse(json));
        Assert.Contains("MinCanId", ex.Message);
    }

    [Fact]
    public void Parse_MaxCanIdOverflow_Throws()
    {
        // L1: max 越界会使过滤恒假 —— 必须校验。
        var json = """{ "targetChannel": "USB2", "maxCanId": 536870912 }""";
        var ex = Assert.Throws<ArgumentException>(() => GatewayConfigLoader.Parse(json));
        Assert.Contains("MaxCanId", ex.Message);
    }
}
