using FluentAssertions;
using PeakCan.Host.Infrastructure.Zlg;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>Probe：ZLG handle 范围、设备信息校验、设备引用释放。</summary>
public sealed class ZlgChannelProbeTests
{
    [Theory]
    [InlineData(0x0051)]
    [InlineData(0x0100)]
    [InlineData(0x7FFF)]
    public void Probe_NonZlgHandle_ReturnsFalse(ushort handle)
    {
        var api = new FakeZlgApi();
        var probe = new ZlgChannelProbe(api, new ZlgDeviceManager(api));

        probe.Probe(handle).Ok.Should().BeFalse();
    }

    [Fact]
    public void Probe_ValidChannel_ReturnsTrueAndReleasesDevice()
    {
        var api = new FakeZlgApi { CanNum = 2 };
        api.Devices[(ZlgDeviceType.USBCANFD_200U, 0)] = api.DefaultDeviceHandle;
        var probe = new ZlgChannelProbe(api, new ZlgDeviceManager(api));

        var result = probe.Probe(0xA900);

        result.Ok.Should().BeTrue();
        api.GetDeviceInfException.Should().BeNull();
        api.CloseCalls.Should().ContainSingle();
    }

    [Fact]
    public void Probe_ChannelOutOfRange_ReturnsFalse()
    {
        var api = new FakeZlgApi { CanNum = 2 };
        api.Devices[(ZlgDeviceType.USBCANFD_200U, 0)] = api.DefaultDeviceHandle;
        var probe = new ZlgChannelProbe(api, new ZlgDeviceManager(api));

        probe.Probe(0xA902).Ok.Should().BeFalse();
    }

    [Fact]
    public void Probe_GetDeviceInfFails_ReturnsFalseAndReleases()
    {
        var api = new FakeZlgApi { GetDeviceInfResult = ZlgError.Failed };
        api.Devices[(ZlgDeviceType.USBCANFD_200U, 0)] = api.DefaultDeviceHandle;
        var probe = new ZlgChannelProbe(api, new ZlgDeviceManager(api));

        probe.Probe(0xA900).Ok.Should().BeFalse();
        api.CloseCalls.Should().ContainSingle();
    }

    [Fact]
    public void Probe_DeviceMissing_ReturnsFalse()
    {
        var api = new FakeZlgApi();
        var probe = new ZlgChannelProbe(api, new ZlgDeviceManager(api));

        probe.Probe(0xA900).Ok.Should().BeFalse();
    }

    [Fact]
    public void Probe_BadImageFormat_ReportsBitnessProblem()
    {
        var api = new FakeZlgApi { OpenDeviceException = new BadImageFormatException("wrong arch") };
        var probe = new ZlgChannelProbe(api, new ZlgDeviceManager(api));

        var result = probe.Probe(0xA900);

        result.Ok.Should().BeFalse();
        result.Message.Should().Contain("x86");
    }
}
