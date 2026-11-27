using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Composition;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Devices;
using PeakCan.Host.Infrastructure.Composite;
using PeakCan.Host.Infrastructure.Peak;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Peak;

/// <summary>
/// 装配守卫：PEAK provider 只能看到 PEAK 自己的通道。
/// 若注入复合枚举器，ZLG 通道会被算进 PEAK 描述符，界面在只插 ZLG 时
/// 仍会默认选中 PEAK，并触发 PEAK 驱动初始化。
/// </summary>
public sealed class PeakProviderCompositionTests
{
    [Fact]
    public void Constructor_RejectsCompositeEnumerator()
    {
        var composite = new CompositeChannelEnumerator(new[]
        {
            Substitute.For<IChannelEnumerator>(),
        });

        var act = () => new PeakCanDeviceProvider(
            composite, NullLogger<PeakCanDeviceProvider>.Instance);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*composite*");
    }

    [Fact]
    public void AppHost_Registers_PeakProvider_With_PeakOnlyEnumerator()
    {
        using var host = new AppHostBuilder().Build();

        var peak = host.Services.GetServices<ICanDeviceProvider>()
            .OfType<PeakCanDeviceProvider>()
            .Should().ContainSingle().Subject;

        peak.Enumerator.Should().BeOfType<PeakChannelEnumerator>(
            "a composite enumerator leaks ZLG handles into the PEAK entry");
    }
}
