using FluentAssertions;
using PeakCan.Host.Infrastructure.Zlg;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>
/// Tests for <see cref="ZlgChannelEnumerator"/>.
/// Pure logic tests (no hardware) for the handle encoding and decoding.
/// </summary>
public sealed class ZlgChannelEnumeratorTests
{
    // ── EncodeHandle ──

    [Theory]
    [InlineData(0, 0, 0, 0x8000)]
    [InlineData(1, 0, 0, 0x8100)]
    [InlineData(70, 0, 0, 0xC600)] // USBCANFD_200U: 70 << 8 = 0x4600
    [InlineData(0, 1, 0, 0x8010)]
    [InlineData(0, 0, 1, 0x8001)]
    [InlineData(70, 3, 1, 0xC631)]
    public void EncodeHandle_Encodes_DevType_DevIdx_CanIdx(uint devType, uint devIdx, uint canIdx, ushort expected)
    {
        ZlgChannelEnumerator.EncodeHandle(devType, devIdx, canIdx).Should().Be(expected);
    }

    [Theory]
    [InlineData(0xC600, 70u, 0u, 0u)]
    [InlineData(0xC631, 70u, 3u, 1u)]
    [InlineData(0x8100, 1u, 0u, 0u)]
    [InlineData(0x8010, 0u, 1u, 0u)]
    [InlineData(0x8001, 0u, 0u, 1u)]
    [InlineData(0x8000, 0u, 0u, 0u)]
    public void EncodeHandle_Roundtrip(int handle, uint expectedDevType, uint expectedDevIdx, uint expectedCanIdx)
    {
        // Decode: devType = (handle >> 8) & 0x7F, devIdx = (handle >> 4) & 0x0F, canIdx = handle & 0x0F
        var h = (ushort)handle;
        var devType = (uint)((h >> 8) & 0x7F);
        var devIdx = (uint)((h >> 4) & 0x0F);
        var canIdx = (uint)(h & 0x0F);

        devType.Should().Be(expectedDevType);
        devIdx.Should().Be(expectedDevIdx);
        canIdx.Should().Be(expectedCanIdx);

        // Re-encode should match
        ZlgChannelEnumerator.EncodeHandle(devType, devIdx, canIdx).Should().Be(h);
    }

    [Fact]
    public void EncodeHandle_DevType70_ProducesC600()
    {
        // 70 << 8 = 0x4600, so 0x8000 | 0x4600 = 0xC600
        var handle = ZlgChannelEnumerator.EncodeHandle(70, 0, 0);
        handle.Should().Be(0xC600);
        // The high bit (0x8000) must be set to distinguish from PEAK handles
        (handle & 0x8000).Should().Be(0x8000);
    }

    [Fact]
    public void EncodeHandle_AllMax_ProducesMaxHandle()
    {
        // devType max 7 bits = 127, devIdx max 4 bits = 15, canIdx max 4 bits = 15
        var handle = ZlgChannelEnumerator.EncodeHandle(127, 15, 15);
        handle.Should().Be(0xFFFF);
    }

    [Fact]
    public void EncodeHandle_ZeroHandle_HasZlgBit()
    {
        // Even when all components are zero, the ZLG bit (0x8000) must be set
        var handle = ZlgChannelEnumerator.EncodeHandle(0, 0, 0);
        (handle & 0x8000).Should().Be(0x8000);
        handle.Should().Be(0x8000);
    }

    // ── Device type constants ──

    [Fact]
    public void DeviceType_UsbCanFd200U_Is70()
    {
        ZlgDeviceType.USBCANFD_200U.Should().Be(70);
    }

    [Fact]
    public void DeviceType_UsbCanFd_Is21()
    {
        ZlgDeviceType.USBCANFD.Should().Be(21);
    }

    [Fact]
    public void DeviceType_UsbCan2_Is4()
    {
        ZlgDeviceType.USBCAN2.Should().Be(4);
    }

    [Fact]
    public void DeviceType_UsbCan1_Is1()
    {
        ZlgDeviceType.USBCAN1.Should().Be(1);
    }
}