using FluentAssertions;
using PeakCan.Host.Infrastructure.Zlg;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>
/// Tests for <see cref="ZlgDeviceManager"/>.
///
/// These tests run against the actual zlgcan.dll. On a machine without
/// ZLG USB hardware / driver installed, ZCAN_OpenDevice returns 0 (failure),
/// so the AcquireDevice tests verify the failure path. The ref-counting
/// logic and Dispose cleanup are still exercised.
/// </summary>
public sealed class ZlgDeviceManagerTests
{
    [Fact]
    public void AcquireDevice_NoHardware_ReturnsFailed()
    {
        using var mgr = new ZlgDeviceManager();
        var ret = mgr.AcquireDevice(ZlgDeviceType.USBCANFD_200U, 0);
        // Without hardware, ZCAN_OpenDevice returns 0 (failed)
        ret.Should().Be(ZlgError.Failed);
    }

    [Fact]
    public void AcquireDevice_NullDeviceType_DoesNotThrow()
    {
        using var mgr = new ZlgDeviceManager();
        // Various device types should not throw even without hardware
        var act = () => mgr.AcquireDevice(ZlgDeviceType.USBCANFD, 0);
        act.Should().NotThrow();
    }

    [Fact]
    public void ReleaseDevice_WithoutAcquire_ReturnsFailed()
    {
        using var mgr = new ZlgDeviceManager();
        var ret = mgr.ReleaseDevice(ZlgDeviceType.USBCANFD_200U, 0);
        ret.Should().Be(ZlgError.Failed);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var mgr = new ZlgDeviceManager();
        // Acquire something first (will fail, but internal state is set up)
        mgr.AcquireDevice(ZlgDeviceType.USBCANFD_200U, 0);
        // Dispose should not throw
        var act = () => mgr.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void AcquireDevice_AfterDispose_ReturnsFailed()
    {
        var mgr = new ZlgDeviceManager();
        mgr.Dispose();
        var ret = mgr.AcquireDevice(ZlgDeviceType.USBCANFD_200U, 0);
        ret.Should().Be(ZlgError.Failed);
    }

    [Fact]
    public void ReleaseDevice_AfterDispose_ReturnsFailed()
    {
        var mgr = new ZlgDeviceManager();
        mgr.Dispose();
        var ret = mgr.ReleaseDevice(ZlgDeviceType.USBCANFD_200U, 0);
        ret.Should().Be(ZlgError.Failed);
    }

    [Fact]
    public void ConcurrentAcquire_DoesNotThrow()
    {
        using var mgr = new ZlgDeviceManager();
        // Simulate concurrent access from multiple channels
        var tasks = Enumerable.Range(0, 10).Select(_ =>
            Task.Run(() => mgr.AcquireDevice(ZlgDeviceType.USBCANFD_200U, 0)));
        var act = () => Task.WhenAll(tasks).Wait();
        act.Should().NotThrow();
    }
}