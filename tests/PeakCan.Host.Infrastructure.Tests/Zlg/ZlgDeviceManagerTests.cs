using FluentAssertions;
using PeakCan.Host.Infrastructure.Zlg;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>
/// 引用计数与句柄语义。全部走 fake IZlgApi，不依赖真实 DLL/硬件。
/// </summary>
public sealed class ZlgDeviceManagerTests
{
    private static FakeZlgApi ApiWithDevice(uint devType = ZlgDeviceType.USBCANFD_200U, uint devIdx = 0)
    {
        var api = new FakeZlgApi();
        api.Devices[(devType, devIdx)] = api.DefaultDeviceHandle;
        return api;
    }

    [Fact]
    public void TryAcquire_FirstCall_OpensDeviceAndReturnsHandle()
    {
        var api = ApiWithDevice();
        using var manager = new ZlgDeviceManager(api);

        manager.TryAcquire(ZlgDeviceType.USBCANFD_200U, 0, out var handle).Should().BeTrue();

        handle.Should().Be(api.DefaultDeviceHandle);
        api.OpenCalls.Should().ContainSingle();
    }

    [Fact]
    public void TryAcquire_SecondCall_ReusesHandleWithoutReopening()
    {
        var api = ApiWithDevice();
        using var manager = new ZlgDeviceManager(api);

        manager.TryAcquire(ZlgDeviceType.USBCANFD_200U, 0, out var first).Should().BeTrue();
        manager.TryAcquire(ZlgDeviceType.USBCANFD_200U, 0, out var second).Should().BeTrue();

        first.Should().Be(second);
        api.OpenCalls.Should().ContainSingle("the device was already open");
    }

    [Fact]
    public void Release_OnlyLastReferenceClosesDevice()
    {
        var api = ApiWithDevice();
        using var manager = new ZlgDeviceManager(api);
        manager.TryAcquire(ZlgDeviceType.USBCANFD_200U, 0, out _);
        manager.TryAcquire(ZlgDeviceType.USBCANFD_200U, 0, out _);

        manager.Release(ZlgDeviceType.USBCANFD_200U, 0).Should().BeTrue();
        api.CloseCalls.Should().BeEmpty();

        manager.Release(ZlgDeviceType.USBCANFD_200U, 0).Should().BeTrue();
        api.CloseCalls.Should().ContainSingle().Which.Should().Be(api.DefaultDeviceHandle);
    }

    [Fact]
    public void TryAcquire_OpenFails_ReturnsFalse()
    {
        var api = new FakeZlgApi();
        using var manager = new ZlgDeviceManager(api);

        manager.TryAcquire(ZlgDeviceType.USBCANFD_200U, 0, out var handle).Should().BeFalse();

        handle.Should().Be(IntPtr.Zero);
        api.OpenCalls.Should().ContainSingle();
        api.CloseCalls.Should().BeEmpty();
    }

    [Fact]
    public void Release_WithoutAcquire_ReturnsFalse()
    {
        using var manager = new ZlgDeviceManager(new FakeZlgApi());

        manager.Release(ZlgDeviceType.USBCANFD_200U, 0).Should().BeFalse();
    }

    [Fact]
    public void Dispose_ClosesEveryOpenDeviceOnce()
    {
        var api = ApiWithDevice();
        api.Devices[(ZlgDeviceType.USBCANFD_100U, 1)] = (IntPtr)0x1010;
        var manager = new ZlgDeviceManager(api);
        manager.TryAcquire(ZlgDeviceType.USBCANFD_200U, 0, out _);
        manager.TryAcquire(ZlgDeviceType.USBCANFD_100U, 1, out _);

        manager.Dispose();
        manager.Dispose();

        api.CloseCalls.Should().HaveCount(2);
    }

    [Fact]
    public void AfterDispose_AcquireAndReleaseFail()
    {
        var api = ApiWithDevice();
        var manager = new ZlgDeviceManager(api);
        manager.Dispose();

        manager.TryAcquire(ZlgDeviceType.USBCANFD_200U, 0, out _).Should().BeFalse();
        manager.Release(ZlgDeviceType.USBCANFD_200U, 0).Should().BeFalse();
    }

    [Fact]
    public void ConcurrentAcquire_OpensDeviceOnce()
    {
        var api = ApiWithDevice();
        using var manager = new ZlgDeviceManager(api);

        var results = new bool[10];
        Parallel.For(0, results.Length, i =>
            results[i] = manager.TryAcquire(ZlgDeviceType.USBCANFD_200U, 0, out _));

        results.Should().OnlyContain(ok => ok);
        api.OpenCalls.Should().ContainSingle();
    }
}
