using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Zlg;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Zlg;

/// <summary>
/// Unit tests for <see cref="ZlgErrorMapper"/>.
/// </summary>
public sealed class ZlgErrorMapperTests
{
    [Fact]
    public void IsOk_ReturnsTrue_ForSuccess()
    {
        ZlgErrorMapper.IsOk(ZlgError.Success).Should().BeTrue();
    }

    [Fact]
    public void IsOk_ReturnsFalse_ForFailed()
    {
        ZlgErrorMapper.IsOk(ZlgError.Failed).Should().BeFalse();
    }

    [Fact]
    public void IsOk_ReturnsFalse_ForUnknown()
    {
        ZlgErrorMapper.IsOk(42).Should().BeFalse();
    }

    [Fact]
    public void ToErrorCode_Success_ReturnsOk()
    {
        var (code, msg) = ZlgErrorMapper.ToErrorCode(ZlgError.Success);
        code.Should().Be(ErrorCode.Ok);
        msg.Should().Be("OK");
    }

    [Fact]
    public void ToErrorCode_Failed_ReturnsUnknown()
    {
        var (code, msg) = ZlgErrorMapper.ToErrorCode(ZlgError.Failed);
        code.Should().Be(ErrorCode.Unknown);
        msg.Should().Be("Operation failed");
    }

    [Fact]
    public void ToErrorCode_UnknownValue_ReturnsUnknownWithHex()
    {
        var (code, msg) = ZlgErrorMapper.ToErrorCode(0xDEAD);
        code.Should().Be(ErrorCode.Unknown);
        msg.Should().Contain("0x0000DEAD");
    }
}