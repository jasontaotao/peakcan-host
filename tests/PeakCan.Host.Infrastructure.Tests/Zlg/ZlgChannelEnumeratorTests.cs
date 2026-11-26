using FluentAssertions;
using PeakCan.Host.Infrastructure.Zlg;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>
/// Handle 编码与新设备常量；枚举走 fake API，不依赖硬件。
/// </summary>
public sealed class ZlgChannelEnumeratorTests
{
    [Theory]
    [InlineData(41u, 0u, 0u, 0xA900)] // USBCANFD_200U
    [InlineData(42u, 0u, 0u, 0xAA00)] // USBCANFD_100U
    [InlineData(43u, 0u, 0u, 0xAB00)] // USBCANFD_MINI
    [InlineData(59u, 0u, 0u, 0xBB00)] // USBCANFD_800U
    [InlineData(76u, 0u, 0u, 0xCC00)] // USBCANFD_400U
    [InlineData(76u, 3u, 1u, 0xCC31)]
    [InlineData(127u, 15u, 15u, 0xFFFF)]
    [InlineData(0u, 0u, 0u, 0x8000)]
    public void EncodeHandle_EncodesComponents(uint devType, uint devIdx, uint canIdx, ushort expected)
    {
        ZlgChannelEnumerator.EncodeHandle(devType, devIdx, canIdx).Should().Be(expected);
    }

    [Fact]
    public void EncodeHandle_TruncatesDevTypeTo7Bits()
    {
        // 128 & 0x7F == 0，只剩 ZLG 标志位。
        ZlgChannelEnumerator.EncodeHandle(128, 0, 0).Should().Be(0x8000);
    }

    [Fact]
    public void Enumerate_DevicePresent_ReturnsAllChannelsWithNewConstants()
    {
        var api = new FakeZlgApi { CanNum = 2 };
        api.Devices[(ZlgDeviceType.USBCANFD_200U, 0)] = api.DefaultDeviceHandle;
        using var manager = new ZlgDeviceManager(api);
        var enumerator = new ZlgChannelEnumerator(api, manager);

        var channels = enumerator.Enumerate();

        channels.Should().HaveCount(2);
        channels.Select(c => c.Handle).Should().Equal(0xA900, 0xA901);
        channels[0].Name.Should().Contain("USBCANFD-200U");
        enumerator.LastDiagnostic.Should().BeNull();
        api.CloseCalls.Should().ContainSingle("enumeration opened and released the device");
    }

    [Fact]
    public void Enumerate_NoDevice_ReturnsEmptyWithoutClosing()
    {
        var api = new FakeZlgApi();
        using var manager = new ZlgDeviceManager(api);
        var enumerator = new ZlgChannelEnumerator(api, manager);

        enumerator.Enumerate().Should().BeEmpty();
        api.CloseCalls.Should().BeEmpty();
    }

    [Fact]
    public void Enumerate_GetDeviceInfFails_ReturnsEmptyWithoutFabricatingChannels()
    {
        var api = new FakeZlgApi { GetDeviceInfResult = ZlgError.Failed };
        api.Devices[(ZlgDeviceType.USBCANFD_200U, 0)] = api.DefaultDeviceHandle;
        using var manager = new ZlgDeviceManager(api);
        var enumerator = new ZlgChannelEnumerator(api, manager);

        enumerator.Enumerate().Should().BeEmpty();
        api.CloseCalls.Should().ContainSingle();
    }

    [Fact]
    public void Enumerate_ZeroChannels_ReturnsEmpty()
    {
        var api = new FakeZlgApi { CanNum = 0 };
        api.Devices[(ZlgDeviceType.USBCANFD_200U, 0)] = api.DefaultDeviceHandle;
        using var manager = new ZlgDeviceManager(api);
        var enumerator = new ZlgChannelEnumerator(api, manager);

        enumerator.Enumerate().Should().BeEmpty();
    }

    [Fact]
    public void Enumerate_DllNotFound_SetsDiagnostic()
    {
        var api = new FakeZlgApi { OpenDeviceException = new DllNotFoundException("missing") };
        using var manager = new ZlgDeviceManager(api);
        var enumerator = new ZlgChannelEnumerator(api, manager);

        enumerator.Enumerate().Should().BeEmpty();
        enumerator.LastDiagnostic.Should().Contain("zlgcan.dll");
    }

    [Fact]
    public void Enumerate_BadImageFormat_SetsBitnessDiagnostic()
    {
        var api = new FakeZlgApi { OpenDeviceException = new BadImageFormatException("wrong arch") };
        using var manager = new ZlgDeviceManager(api);
        var enumerator = new ZlgChannelEnumerator(api, manager);

        enumerator.Enumerate().Should().BeEmpty();
        enumerator.LastDiagnostic.Should().Contain("32");
    }

    [Fact]
    public void Enumerate_UnexpectedException_DoesNotThrowAndSetsDiagnostic()
    {
        var api = new FakeZlgApi
        {
            OpenDeviceException = new InvalidOperationException("driver fault"),
        };
        using var manager = new ZlgDeviceManager(api);
        var enumerator = new ZlgChannelEnumerator(api, manager);

        var act = () => enumerator.Enumerate();

        act.Should().NotThrow("IChannelEnumerator 契约要求 Non-throwing");
        enumerator.LastDiagnostic.Should().Contain("InvalidOperationException");
    }

    [Fact]
    public void Enumerate_DoesNotCloseDeviceHeldByActiveChannel()
    {
        var api = new FakeZlgApi { CanNum = 2 };
        api.Devices[(ZlgDeviceType.USBCANFD_200U, 0)] = api.DefaultDeviceHandle;
        using var manager = new ZlgDeviceManager(api);
        manager.TryAcquire(ZlgDeviceType.USBCANFD_200U, 0, out _);

        var enumerator = new ZlgChannelEnumerator(api, manager);
        enumerator.Enumerate();

        api.CloseCalls.Should().BeEmpty("the active channel still holds a reference");
        manager.Release(ZlgDeviceType.USBCANFD_200U, 0);
        api.CloseCalls.Should().ContainSingle();
    }
}
