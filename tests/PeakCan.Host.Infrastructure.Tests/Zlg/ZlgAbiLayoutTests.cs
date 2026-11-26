using System.Runtime.InteropServices;
using FluentAssertions;
using PeakCan.Host.Infrastructure.Zlg;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>
/// 钉死 ZCAN ABI 的字节布局。这些数字来自厂商 zlgcan.h / canframe.h；
/// 任何 padding/字段顺序漂移都必须让 CI 变红，而不是等到真机上读错帧。
/// </summary>
public sealed class ZlgAbiLayoutTests
{
    [Fact]
    public void Sizes_MatchVendorHeader()
    {
        Marshal.SizeOf<ZcanCanFrame>().Should().Be(16);
        Marshal.SizeOf<ZcanCanFdFrame>().Should().Be(72);
        Marshal.SizeOf<ZcanReceiveData>().Should().Be(24);
        Marshal.SizeOf<ZcanReceiveFdData>().Should().Be(80);
        Marshal.SizeOf<ZcanTransmitData>().Should().Be(20);
        Marshal.SizeOf<ZcanTransmitFdData>().Should().Be(76);
        Marshal.SizeOf<ZcanCanInit>().Should().Be(16);
        Marshal.SizeOf<ZcanCanFdInit>().Should().Be(28);
        Marshal.SizeOf<ZcanChannelInitConfig>().Should().Be(32);
        Marshal.SizeOf<ZcanDeviceInfo>().Should().Be(80);
    }

    [Fact]
    public void Offsets_MatchVendorHeader()
    {
        Marshal.OffsetOf<ZcanReceiveData>(nameof(ZcanReceiveData.Timestamp))
            .ToInt32().Should().Be(16);
        Marshal.OffsetOf<ZcanReceiveFdData>(nameof(ZcanReceiveFdData.Timestamp))
            .ToInt32().Should().Be(72);
        Marshal.OffsetOf<ZcanCanFdInit>(nameof(ZcanCanFdInit.Reserved))
            .ToInt32().Should().Be(24);
        Marshal.OffsetOf<ZcanChannelInitConfig>(nameof(ZcanChannelInitConfig.Can))
            .ToInt32().Should().Be(4);
        Marshal.OffsetOf<ZcanChannelInitConfig>(nameof(ZcanChannelInitConfig.CanFd))
            .ToInt32().Should().Be(4);
        Marshal.OffsetOf<ZcanDeviceInfo>(nameof(ZcanDeviceInfo.HwType))
            .ToInt32().Should().Be(31);
        Marshal.OffsetOf<ZcanDeviceInfo>(nameof(ZcanDeviceInfo.Reserved))
            .ToInt32().Should().Be(72);
    }

    [Fact]
    public void DeviceTypes_MatchVendorHeader()
    {
        ZlgDeviceType.USBCANFD_200U.Should().Be(41);
        ZlgDeviceType.USBCANFD_100U.Should().Be(42);
        ZlgDeviceType.USBCANFD_MINI.Should().Be(43);
        ZlgDeviceType.USBCANFD_800U.Should().Be(59);
        ZlgDeviceType.USBCANFD_400U.Should().Be(76);
        ZlgDeviceType.All.Should().Equal(41u, 42u, 43u, 59u, 76u);
    }

    [Fact]
    public void IdFlagBits_MatchVendorHeader()
    {
        ZcanCanIdFlags.Extended.Should().Be(0x8000_0000);
        ZcanCanIdFlags.Remote.Should().Be(0x4000_0000);
        ZcanCanIdFlags.Error.Should().Be(0x2000_0000);
        ZcanCanIdFlags.IdMask.Should().Be(0x1FFF_FFFF);
        ZcanCanFdFlags.Brs.Should().Be(0x01);
        ZcanCanFdFlags.Esi.Should().Be(0x02);
    }
}
