using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Statistics;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

/// <summary>
/// 2026-09-06 设计层 MEDIUM 修复：BaudRate 预设 → 标称波特率映射。
/// 覆盖全部 7 个 Core 预设 + 自定义 FD 名解析回退 + 未知名兜底。
/// </summary>
public class BaudRateMapTests
{
    [Theory]
    [InlineData(125_000)]
    [InlineData(250_000)]
    [InlineData(500_000)]
    [InlineData(1_000_000)]
    public void Classic_Presets_Map_To_Nominal_Bps(long expected)
    {
        BaudRate preset = expected switch
        {
            125_000 => BaudRate.Can125kbps,
            250_000 => BaudRate.Can250kbps,
            500_000 => BaudRate.Can500kbps,
            _ => BaudRate.Can1Mbps,
        };
        BaudRateMap.NominalBps(preset).Should().Be(expected);
    }

    [Fact]
    public void Fd_Presets_Map_To_Nominal_Phase_1Mbps()
    {
        // FD 数据相位（1/2/5 Mbps）不影响标称相位；负载按标称归一。
        BaudRateMap.NominalBps(BaudRate.CanFd1Mbps).Should().Be(1_000_000);
        BaudRateMap.NominalBps(BaudRate.CanFd2Mbps).Should().Be(1_000_000);
        BaudRateMap.NominalBps(BaudRate.CanFd5Mbps).Should().Be(1_000_000);
    }

    [Fact]
    public void Unknown_Name_With_Parseable_Rate_Falls_Back_To_Parse()
    {
        var custom = new BaudRate("f_clock_mhz=20, ...", "250 kbps", false);
        BaudRateMap.NominalBps(custom).Should().Be(250_000);
    }

    [Fact]
    public void Unparseable_Name_Falls_Back_To_Default_1Mbps()
    {
        var custom = new BaudRate("f_clock_mhz=20, ...", "custom rate", true);
        BaudRateMap.NominalBps(custom).Should().Be(BaudRateMap.DefaultBps);
    }
}
